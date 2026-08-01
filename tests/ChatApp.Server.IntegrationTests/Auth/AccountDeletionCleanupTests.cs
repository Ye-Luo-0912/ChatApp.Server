using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
using Core.Models.Attachment;
using Core.Models.Export;
using Core.Models.Friend;
using Core.Models.Identity;
using Core.Models.Moderation;
using Core.Models.Security;
using Infrastructure.Data;
using Infrastructure.Services;
using Infrastructure.Services.Auth;
using Infrastructure.Services.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisPostgresCollection))]
public sealed class AccountDeletionCleanupTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [SkippableFact]
    public async Task ProcessDueDeletions_CascadesRelatedData()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var (victim, peer, lifecycle) = await SeedScheduledUserAsync(db, "del");
        SeedRelated(db, victim.Id, peer.Id);
        await db.SaveChangesAsync();

        var processed = await lifecycle.ProcessDueDeletionsAsync();
        Assert.True(processed >= 1);

        Assert.False(await db.Users.AnyAsync(u => u.Id == victim.Id));
        Assert.False(await db.Friendships.AnyAsync(f => f.UserId == victim.Id || f.FriendId == victim.Id));
        Assert.False(await db.InAppNotifications.AnyAsync(n => n.UserId == victim.Id));
        Assert.False(await db.SecurityEvents.AnyAsync(e => e.UserId == victim.Id));
        Assert.False(await db.UserReports.AnyAsync(r => r.TargetUserId == victim.Id || r.ReporterId == victim.Id));
        Assert.False(await db.TrustedDevices.AnyAsync(d => d.UserId == victim.Id));
        Assert.True(await db.Users.AnyAsync(u => u.Id == peer.Id));
        Assert.True(await db.RealtimeOutbox.AnyAsync(o => o.PayloadJson!.Contains(victim.Id.ToString())));
        Assert.True(await db.AccountCleanupSagas.AnyAsync(s =>
            s.UserId == victim.Id && s.Status == Core.Models.Export.AccountCleanupSagaStatus.Pending));
    }

    [SkippableFact]
    public async Task AccountDeletion_CommitsAttachmentTombstoneWithUserDeletion()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var key = $"attachments/account-delete-{Guid.NewGuid():N}";
        var metadata = new AvailableEmptyAttachmentMetadataStore
        {
            ObjectKeys = [key],
        };
        var (victim, _, lifecycle) = await SeedScheduledUserAsync(
            db, "blob", metadata);

        Assert.Equal(1, await lifecycle.ProcessDueDeletionsAsync());
        Assert.False(await db.Users.AsNoTracking().AnyAsync(user => user.Id == victim.Id));
        var tombstone = await db.AttachmentBlobDeleteJobs
            .AsNoTracking()
            .SingleAsync(job => job.ObjectKey == key);
        Assert.Equal(victim.Id, tombstone.UserId);
        Assert.Equal(AttachmentBlobDeleteJobStatus.Pending, tombstone.Status);
    }

    [SkippableFact]
    public async Task CancelDeletion_AfterClaim_PreservesRelatedData()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var (victim, peer, lifecycle) = await SeedScheduledUserAsync(db, "cxl");
        SeedRelated(db, victim.Id, peer.Id);
        await db.SaveChangesAsync();

        lifecycle.AfterClaimHook = async (ids, ct) =>
        {
            Assert.Contains(victim.Id, ids);
            // 模拟用户在 Worker 领取租约后、清理前取消
            var cancelDb = postgres.CreateContext();
            var cancelSvc = new AccountLifecycleService(
                cancelDb,
                CreateTokenService(),
                new SecurityEventStore(cancelDb, NullLogger<SecurityEventStore>.Instance),
                new NoopExportBlob(),
                AvailableEmptyAttachmentMetadataStore.Instance,
                new NoopAttachmentBlobDeleteService(),
                NullLogger<AccountLifecycleService>.Instance);
            var cancel = await cancelSvc.CancelDeletionAsync(victim.Id, ct);
            Assert.True(cancel.Succeeded);
        };

        var processed = await lifecycle.ProcessDueDeletionsAsync();
        Assert.Equal(0, processed);

        Assert.True(await db.Users.AsNoTracking().AnyAsync(u => u.Id == victim.Id));
        Assert.Null((await db.Users.AsNoTracking().FirstAsync(u => u.Id == victim.Id)).DeletionScheduledAt);
        Assert.True(await db.Friendships.AsNoTracking().AnyAsync(f => f.UserId == victim.Id));
        Assert.True(await db.InAppNotifications.AsNoTracking().AnyAsync(n => n.UserId == victim.Id));
        Assert.True(await db.SecurityEvents.AsNoTracking().AnyAsync(e => e.UserId == victim.Id));
        Assert.True(await db.TrustedDevices.AsNoTracking().AnyAsync(d => d.UserId == victim.Id));
    }

    [SkippableFact]
    public async Task TwoWorkers_OnlyOnePurges_SameUser()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var (victim, peer, _) = await SeedScheduledUserAsync(db, "race");
        SeedRelated(db, victim.Id, peer.Id);
        await db.SaveChangesAsync();

        await using var dbA = postgres.CreateContext();
        await using var dbB = postgres.CreateContext();
        var workerA = new AccountLifecycleService(
            dbA, CreateTokenService(), new SecurityEventStore(dbA, NullLogger<SecurityEventStore>.Instance),
            new NoopExportBlob(),
            AvailableEmptyAttachmentMetadataStore.Instance,
            new NoopAttachmentBlobDeleteService(),
            NullLogger<AccountLifecycleService>.Instance);
        var workerB = new AccountLifecycleService(
            dbB, CreateTokenService(), new SecurityEventStore(dbB, NullLogger<SecurityEventStore>.Instance),
            new NoopExportBlob(),
            AvailableEmptyAttachmentMetadataStore.Instance,
            new NoopAttachmentBlobDeleteService(),
            NullLogger<AccountLifecycleService>.Instance);

        var results = await Task.WhenAll(
            workerA.ProcessDueDeletionsAsync(),
            workerB.ProcessDueDeletionsAsync());

        Assert.Equal(1, results.Sum());
        Assert.False(await db.Users.AsNoTracking().AnyAsync(u => u.Id == victim.Id));
    }

    [SkippableFact]
    public async Task AdminAudit_IsAnonymized_NotDeleted()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var (victim, peer, lifecycle) = await SeedScheduledUserAsync(db, "aud");
        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            AdminUserId = peer.Id,
            TargetUserId = victim.Id,
            Action = "DisableUser",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.True(await lifecycle.ProcessDueDeletionsAsync() >= 1);

        var audit = await db.AdminAuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == "DisableUser" && a.AdminUserId == peer.Id);
        Assert.Null(audit.TargetUserId);
        Assert.Contains($"anonymized-user:{victim.Id}", audit.Detail ?? "", StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AccountCleanupCompleted_MarksSagaCompleted_AndIsIdempotent()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var (victim, _, lifecycle) = await SeedScheduledUserAsync(db, "saga");
        await db.SaveChangesAsync();

        Assert.True(await lifecycle.ProcessDueDeletionsAsync() >= 1);

        var saga = await db.AccountCleanupSagas.AsNoTracking()
            .SingleAsync(s => s.UserId == victim.Id);
        Assert.Equal(Core.Models.Export.AccountCleanupSagaStatus.Pending, saga.Status);

        var completer = new AccountCleanupSagaService(
            db, NullLogger<AccountCleanupSagaService>.Instance);
        var completedEventId = $"cleanup-done:{saga.EventId}";

        Assert.Equal(
            Core.Models.Export.AccountCleanupApplyResult.Completed,
            await completer.TryCompleteAsync(victim.Id, completedEventId));
        Assert.Equal(
            Core.Models.Export.AccountCleanupApplyResult.DuplicateDelivery,
            await completer.TryCompleteAsync(victim.Id, completedEventId));

        var done = await db.AccountCleanupSagas.AsNoTracking()
            .SingleAsync(s => s.UserId == victim.Id);
        Assert.Equal(Core.Models.Export.AccountCleanupSagaStatus.Completed, done.Status);
        Assert.NotNull(done.CompletedAt);
        Assert.Null(done.LastError);
    }

    [SkippableFact]
    public async Task AccountCleanupSaga_FailStalePending_MarksFailed()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var userId = new TsidGeneratorService().GenerateTsid();
        db.AccountCleanupSagas.Add(new Core.Models.Export.AccountCleanupSaga
        {
            UserId = userId,
            EventId = Guid.NewGuid().ToString("N"),
            Status = Core.Models.Export.AccountCleanupSagaStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-5),
        });
        await db.SaveChangesAsync();

        var svc = new AccountCleanupSagaService(db, NullLogger<AccountCleanupSagaService>.Instance);
        var affected = await svc.FailStalePendingAsync(TimeSpan.FromHours(72));
        Assert.True(affected >= 1);

        var saga = await db.AccountCleanupSagas.AsNoTracking().SingleAsync(s => s.UserId == userId);
        Assert.Equal(Core.Models.Export.AccountCleanupSagaStatus.Failed, saga.Status);
        Assert.Equal("pending_timeout", saga.LastError);
        Assert.NotNull(saga.CompletedAt);
    }

    private async Task<(ApplicationUser Victim, ApplicationUser Peer, AccountLifecycleService Lifecycle)>
        SeedScheduledUserAsync(
            UserDbContext db, string prefix, IAttachmentMetadataStore? metadata = null)
    {
        var tsid = new TsidGeneratorService();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var hasher = AuthTestFactories.CreatePasswordHasher();
        var victim = new ApplicationUser
        {
            Id = tsid.GenerateTsid(),
            UserName = $"{prefix}-{suffix}",
            NormalizedUserName = $"{prefix}-{suffix}".ToUpperInvariant(),
            Email = $"{prefix}-{suffix}@ex.com",
            NormalizedEmail = $"{prefix}-{suffix}@EX.COM",
            EmailConfirmed = true,
            PasswordHash = await hasher.HashPasswordAsync("Passw0rd!"),
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
            DeletionScheduledAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        var peer = new ApplicationUser
        {
            Id = tsid.GenerateTsid(),
            UserName = $"{prefix}-p-{suffix}",
            NormalizedUserName = $"{prefix}-P-{suffix}".ToUpperInvariant(),
            Email = $"{prefix}-p-{suffix}@ex.com",
            NormalizedEmail = $"{prefix}-P-{suffix}@EX.COM",
            EmailConfirmed = true,
            PasswordHash = await hasher.HashPasswordAsync("Passw0rd!"),
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
        };
        db.Users.AddRange(victim, peer);
        await db.SaveChangesAsync();

        var lifecycle = new AccountLifecycleService(
            db,
            CreateTokenService(),
            new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance),
            new NoopExportBlob(),
            metadata ?? AvailableEmptyAttachmentMetadataStore.Instance,
            new NoopAttachmentBlobDeleteService(),
            NullLogger<AccountLifecycleService>.Instance);
        return (victim, peer, lifecycle);
    }

    private static void SeedRelated(UserDbContext db, long victimId, long peerId)
    {
        var tsid = new TsidGeneratorService();
        db.Friendships.Add(new UserFriendEntry
        {
            FriendshipId = tsid.GenerateTsid(),
            UserId = victimId,
            FriendId = peerId,
        });
        db.InAppNotifications.Add(new InAppNotification
        {
            UserId = victimId,
            Type = "security",
            Title = "t",
            Body = "b",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SecurityEvents.Add(new SecurityEvent
        {
            UserId = victimId,
            EventType = SecurityEventType.LoginSuccess,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.UserReports.Add(new UserReport
        {
            ReporterId = peerId,
            TargetType = UserReportTargetType.User,
            TargetUserId = victimId,
            Reason = "spam",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.TrustedDevices.Add(new TrustedDevice
        {
            UserId = victimId,
            TokenHash = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                .ToLowerInvariant(),
            DeviceIdHint = "hint",
            Label = "test",
            TrustedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
    }

    private TokenService CreateTokenService()
        => new(
            redis.Cache,
            redis.Cache,
            redis.Cache,
            new FixedDeviceInfo("del-device"),
            Options.Create(new Core.Settings.JwtSettings
            {
                AccessTokenExpirationMinutes = 30,
                RefreshTokenLength = 32,
                RefreshTokenExpirationDays = 3,
                Issuer = "ChatApp",
                Audience = "ChatApp",
                Secret = "test-deletion-jwt-secret-please-change",
            }),
            NullLogger<TokenService>.Instance);

    private sealed class NoopExportBlob : IDataExportBlobStore
    {
        public Task WriteAsync(string objectKey, Stream content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class AvailableEmptyAttachmentMetadataStore : IAttachmentMetadataStore
    {
        public static AvailableEmptyAttachmentMetadataStore Instance { get; } = new();
        public IReadOnlyList<string> ObjectKeys { get; init; } = [];
        public bool IsAvailable => true;
        public string UnavailableReason => string.Empty;

        public Task<AttachmentUploadReservationStatus> ReserveTicketedAsync(
            string attachmentId, long uploaderUserId, string objectKey, string? publicUrl,
            string contentType, long sizeBytes, string? originalName,
            string? clientAttachmentId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(AttachmentUploadReservationStatus.Reserved);

        public Task ConfirmAsync(
            string attachmentId, long uploaderUserId, string objectKey, string? publicUrl,
            string contentType, long sizeBytes, string? originalName = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkUploadedScanningAsync(
            string attachmentId, long uploaderUserId, long sizeBytes, string? sha256Hex = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkRejectedAsync(
            string attachmentId, long uploaderUserId, string? reason = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AttachmentDownloadAccess> ResolveDownloadAccessAsync(
            string attachmentId, long userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new AttachmentDownloadAccess(
                attachmentId, string.Empty, "application/octet-stream", null,
                AttachmentDownloadDecision.NotFound));

        public Task<IReadOnlyList<AttachmentRecord>> ListForExportAsync(
            long userId, int maxRows = 50_000, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AttachmentRecord>>([]);

        public Task<IReadOnlyList<string>> ListObjectKeysForUserAsync(
            long uploaderUserId, CancellationToken cancellationToken = default)
            => Task.FromResult(ObjectKeys);

        public Task<IReadOnlySet<string>> ListActiveObjectKeysAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));

        public Task MarkAbandonedAsync(
            IReadOnlyList<string> attachmentIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task MarkAbandonedByUploaderAsync(
            long uploaderUserId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string?> TryAbandonUnboundByUploaderAsync(
            string attachmentId, long uploaderUserId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<AttachmentAbandonBatchItem>> AbandonAgedUnboundAsync(
            TimeSpan maxAge, int batchSize, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AttachmentAbandonBatchItem>>([]);

        public Task<AttachmentOpsOrphanQueryResult> QueryOpsOrphansAsync(
            TimeSpan orphanAge, TimeSpan stuckScanningAge, int sampleLimit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AttachmentOpsOrphanQueryResult(
                Available: true,
                UnavailableReason: null,
                ConfirmedUnboundPastAgeCount: 0,
                AbandonedUploadingPastAgeCount: 0,
                StuckScanningCount: 0,
                OldestConfirmedUnboundAtMs: null,
                OldestUploadingAtMs: null,
                OldestStuckScanningAtMs: null,
                ActiveAttachmentCount: 0,
                ActiveSizeBytesSum: 0,
                WorstConfirmedUnbound: [],
                WorstUploading: [],
                WorstStuckScanning: []));
    }

    private sealed class NoopAttachmentBlobDeleteService : IAttachmentBlobDeleteService
    {
        public Task EnqueueAsync(
            IEnumerable<string> objectKeys,
            long? userId = null,
            string? attachmentId = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task EnqueueAsync(
            IEnumerable<(string ObjectKey, string? AttachmentId)> items,
            long? userId = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }
}
