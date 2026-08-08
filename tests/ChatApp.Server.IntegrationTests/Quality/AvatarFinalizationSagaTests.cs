using Core.Interfaces;
using Core.Models.Export;
using Core.Models.Identity;
using Core.Settings;
using Infrastructure.Data;
using Infrastructure.Services;
using Infrastructure.Services.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Quality;

public sealed class AvatarFinalizationSagaTests
{
    [Fact]
    public async Task Finalization_UsesVersionCasAndDurableDeletionStages()
    {
        var dbOptions = new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new UserDbContext(dbOptions);
        db.Users.Add(new ApplicationUser
        {
            Id = 42,
            AvatarUrl = "avatars/42/confirmed/old.jpg",
            AvatarVersion = 1,
            DeletionEpoch = 0,
            AccountState = AccountState.Active,
        });
        await db.SaveChangesAsync();

        var deletes = new RecordingBlobDeletes();
        var storage = new RecordingAvatarStorage();
        var service = new AvatarFinalizationSagaService(
            db,
            storage,
            deletes,
            new PlaintextProtector(),
            Options.Create(new AvatarStorageOptions()),
            NullLogger<AvatarFinalizationSagaService>.Instance);

        var requested = await service.RequestAsync(
            42,
            "avatars/42/pending/upload.bin",
            "ticket");
        Assert.True(requested.Result.Succeeded);
        Assert.NotNull(requested.Response);

        var claimed = await service.ClaimDueAsync(1);
        Assert.Single(claimed);
        Assert.Equal(
            LeaseRenewalResult.Renewed,
            await service.RenewLeaseAsync(claimed[0]));
        await service.ExecuteClaimedAsync(claimed[0]);
        Assert.Equal(AvatarFinalizationSagaStatus.Completed, claimed[0].Status);
        Assert.True(await service.CompleteClaimedAsync(claimed[0]));

        db.ChangeTracker.Clear();
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == 42);
        Assert.Equal("https://cdn.example/avatars/42/confirmed/new.jpg", user.AvatarUrl);
        Assert.Equal(2, user.AvatarVersion);
        Assert.Contains("avatars/42/confirmed/new.jpg", deletes.CandidateKeys);
        Assert.Contains("avatars/42/confirmed/old.jpg", deletes.DeleteKeys);
        Assert.Contains("avatars/42/confirmed/new.jpg", deletes.PublishedKeys);
    }

    [Fact]
    public async Task NonRelationalLeaseRetryAndDeadLetterAreFenced()
    {
        var dbOptions = new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new UserDbContext(dbOptions);
        db.Users.Add(new ApplicationUser
        {
            Id = 42,
            DeletionEpoch = 0,
            AccountState = AccountState.Active,
        });
        await db.SaveChangesAsync();

        var service = new AvatarFinalizationSagaService(
            db,
            new RecordingAvatarStorage(),
            new RecordingBlobDeletes(),
            new PlaintextProtector(),
            Options.Create(new AvatarStorageOptions()),
            NullLogger<AvatarFinalizationSagaService>.Instance);

        var requested = await service.RequestAsync(
            42,
            "avatars/42/pending/retry.bin",
            "ticket");
        Assert.True(requested.Result.Succeeded);
        var retryClaim = Assert.Single(await service.ClaimDueAsync(1));

        Assert.True(await service.RetryClaimedAsync(retryClaim, "temporary failure"));
        db.ChangeTracker.Clear();
        var retried = await db.Set<AvatarFinalizationSaga>()
            .AsNoTracking()
            .SingleAsync(x => x.ObjectKey.EndsWith("retry.bin"));
        Assert.Equal(AvatarFinalizationSagaStatus.Requested, retried.Status);
        Assert.Null(retried.LeaseOwner);
        Assert.Equal("temporary failure", retried.LastError);

        db.Set<AvatarFinalizationSaga>().Add(new AvatarFinalizationSaga
        {
            UserId = 42,
            ObjectKey = "avatars/42/pending/dead.bin",
            Status = AvatarFinalizationSagaStatus.Requested,
            NextAttemptAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var deadClaim = Assert.Single(
            await service.ClaimDueAsync(10),
            x => x.ObjectKey.EndsWith("dead.bin"));
        Assert.True(await service.DeadLetterClaimedAsync(deadClaim, "permanent failure"));
        db.ChangeTracker.Clear();
        var dead = await db.Set<AvatarFinalizationSaga>()
            .AsNoTracking()
            .SingleAsync(x => x.ObjectKey.EndsWith("dead.bin"));
        Assert.Equal(AvatarFinalizationSagaStatus.Failed, dead.Status);
        Assert.Null(dead.LeaseToken);
        Assert.Equal("permanent failure", dead.LastError);
    }

    private sealed class PlaintextProtector : IMfaSecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedOrPlain) => protectedOrPlain;
    }

    private sealed class RecordingAvatarStorage : IAvatarStorage
    {
        public bool IsAllowedContentType(string contentType) => true;
        public long MaxBytes => 1024 * 1024;
        public Task<(string ObjectKey, string Ticket, string UploadUrl, string PublicUrl, DateTimeOffset ExpiresAt)> CreateUploadTicketAsync(long userId, string contentType, long contentLength, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? Error)> StoreAsync(long userId, string ticket, Stream content, string contentType, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? Error)> ConfirmObjectAsync(long userId, string objectKey, string? ticket = null, CancellationToken cancellationToken = default)
            => Task.FromResult<(bool, string?, string?, string?)>((
                true,
                "https://cdn.example/avatars/42/confirmed/new.jpg",
                "avatars/42/confirmed/new.jpg",
                null));
        public Task TryDeleteAsync(string? objectKeyOrUrl, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingBlobDeletes : IAttachmentBlobDeleteService
    {
        public List<string> CandidateKeys { get; } = [];
        public List<string> DeleteKeys { get; } = [];
        public List<string> PublishedKeys { get; } = [];

        public Task EnqueueAsync(IEnumerable<string> objectKeys, long? userId = null, string? attachmentId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task EnqueueAsync(IEnumerable<(string ObjectKey, string? AttachmentId)> items, long? userId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task EnqueueAvatarAsync(IEnumerable<string> objectKeys, long? userId = null, CancellationToken cancellationToken = default)
        {
            DeleteKeys.AddRange(objectKeys);
            return Task.CompletedTask;
        }

        public Task EnqueueAvatarCandidatesAsync(IEnumerable<string> objectKeys, long? userId = null, CancellationToken cancellationToken = default)
        {
            CandidateKeys.AddRange(objectKeys);
            return Task.CompletedTask;
        }

        public Task PublishAvatarCandidatesAsync(IEnumerable<string> objectKeys, long? userId = null, CancellationToken cancellationToken = default)
        {
            PublishedKeys.AddRange(objectKeys);
            return Task.CompletedTask;
        }

        public Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }
}
