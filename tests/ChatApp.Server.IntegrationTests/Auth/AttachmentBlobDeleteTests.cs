using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Data;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

public sealed class AttachmentBlobDeleteTests
{
    [Fact]
    public async Task Enqueue_ThenFailedDelete_StaysPending_WithLastError()
    {
        await using var db = CreateDb();
        var storage = new FlakyAttachmentStorage(failUntilAttempt: 99);
        var svc = CreateService(db, storage);

        await svc.EnqueueAsync(["user/1/a.png"], userId: 42, attachmentId: "att1");

        var deleted = await svc.ProcessDueAsync();
        Assert.Equal(0, deleted);

        var job = await db.AttachmentBlobDeleteJobs.SingleAsync();
        Assert.Equal(AttachmentBlobDeleteJobStatus.Pending, job.Status);
        Assert.True(job.AttemptCount >= 1);
        Assert.False(string.IsNullOrWhiteSpace(job.LastError));
        Assert.True(job.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(-5));
        Assert.Contains("user/1/a.png", storage.DeleteCalls);
    }

    [Fact]
    public async Task WorkerRetry_Succeeds_MarksDone()
    {
        await using var db = CreateDb();
        var storage = new FlakyAttachmentStorage(failUntilAttempt: 1);
        var svc = CreateService(db, storage);

        await svc.EnqueueAsync(["user/1/retry.png"], userId: 7);

        Assert.Equal(0, await svc.ProcessDueAsync());
        var pending = await db.AttachmentBlobDeleteJobs.SingleAsync();
        Assert.Equal(AttachmentBlobDeleteJobStatus.Pending, pending.Status);

        // 到期后再试
        pending.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        Assert.Equal(1, await svc.ProcessDueAsync());
        var done = await db.AttachmentBlobDeleteJobs.SingleAsync();
        Assert.Equal(AttachmentBlobDeleteJobStatus.Done, done.Status);
        Assert.Null(done.LastError);
        Assert.NotNull(done.CompletedAt);
        Assert.Equal(2, storage.DeleteCalls.Count);
    }

    [Fact]
    public async Task AbandonedPng_Orphan_CleanedViaJob()
    {
        var root = Path.Combine(Path.GetTempPath(), "chatapp-att-orphan", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var relative = "9/orphan.png";
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, "png-bytes"u8.ToArray());

        await using var db = CreateDb();
        var storage = new LocalAttachmentStorage(
            Options.Create(new AttachmentStorageOptions
            {
                LocalRootPath = root,
                UsePublicStatic = false,
            }),
            new ChatApp.Server.IntegrationTests.Support.NoopCacheProvider(),
            new ChatApp.Server.IntegrationTests.Support.NoopCacheProvider(),
            NullLogger<LocalAttachmentStorage>.Instance);
        var svc = CreateService(db, storage);

        await svc.EnqueueAsync([relative], userId: 9, attachmentId: "abandoned1");
        Assert.True(File.Exists(full));
        Assert.Equal(1, await svc.ProcessDueAsync());
        Assert.False(File.Exists(full));
        Assert.Equal(AttachmentBlobDeleteJobStatus.Done,
            (await db.AttachmentBlobDeleteJobs.SingleAsync()).Status);
    }

    private static AttachmentBlobDeleteService CreateService(UserDbContext db, IAttachmentStorage storage)
        => new(
            db,
            storage,
            Options.Create(new AttachmentStorageOptions
            {
                MaxDeleteAttempts = 20,
                DeleteBackoffSeconds = 1,
                DeleteBatchSize = 50,
            }),
            NullLogger<AttachmentBlobDeleteService>.Instance);

    private static UserDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new UserDbContext(options);
    }

    private sealed class FlakyAttachmentStorage(int failUntilAttempt) : IAttachmentStorage
    {
        private int _attempts;
        public List<string> DeleteCalls { get; } = [];

        public bool IsAllowedContentType(string contentType) => true;
        public long MaxBytes => 1;

        public Task<(string AttachmentId, string ObjectKey, string Ticket, string UploadUrl, string PublicUrl, DateTimeOffset ExpiresAt)>
            CreateUploadTicketAsync(
                long userId, string contentType, long contentLength,
                string? originalName = null, string? clientAttachmentId = null,
                CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? AttachmentId, long SizeBytes, string? Sha256Hex, string? Error)> StoreAsync(
            long userId, string ticket, Stream content, string contentType, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? AttachmentId, string? ContentType, long SizeBytes, string? OriginalName, string? Error)>
            ConfirmObjectAsync(
                long userId, string objectKey, string? ticket = null, string? attachmentId = null,
                CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public string? TryResolveLocalPhysicalPath(string objectKey) => null;

        public Task<AttachmentReadResult?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
            => Task.FromResult<AttachmentReadResult?>(null);

        public Task<AttachmentSignedUrl?> CreateSignedDownloadUrlAsync(
            string objectKey, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult<AttachmentSignedUrl?>(null);

        public Task DeleteAsync(string objectKeyOrUrl, CancellationToken cancellationToken = default)
        {
            DeleteCalls.Add(objectKeyOrUrl);
            _attempts++;
            if (_attempts <= failUntilAttempt)
                throw new IOException("simulated_delete_failure");
            return Task.CompletedTask;
        }

        public Task TryDeleteAsync(string? objectKeyOrUrl, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
