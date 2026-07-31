using System.Security.Cryptography;
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

public sealed class AttachmentScanRetryTests
{
    [Fact]
    public async Task TransientFail_Retries_ThenConfirms()
    {
        await using var db = CreateDb();
        var storage = new MemoryAttachmentStorage();
        storage.Put("u/1/a.bin", "hello-scan"u8.ToArray());
        var meta = new RecordingMetadataStore();
        var scanner = new FlakyScanner(failTransientUntil: 1);
        var svc = CreateService(db, storage, meta, scanner);

        await svc.EnqueueAsync("att1", 9, "u/1/a.bin", "application/octet-stream", "a.bin", 10);

        Assert.Equal(0, await svc.ProcessDueAsync());
        var pending = await db.AttachmentScanJobs.SingleAsync();
        Assert.Equal(AttachmentScanJobStatus.Pending, pending.Status);
        Assert.True(pending.AttemptCount >= 1);
        Assert.False(string.IsNullOrWhiteSpace(pending.LastError));
        Assert.Empty(meta.Confirmed);
        Assert.Empty(meta.Rejected);

        pending.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        Assert.Equal(1, await svc.ProcessDueAsync());
        var done = await db.AttachmentScanJobs.SingleAsync();
        Assert.Equal(AttachmentScanJobStatus.Done, done.Status);
        Assert.Contains("att1", meta.Confirmed);
        Assert.Empty(meta.Rejected);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData("hello-scan"u8)),
            Assert.Single(meta.UploadedHashes));
    }

    [Fact]
    public async Task StaleScanLease_CannotStageProjectionOrWriteMetadata()
    {
        await using var db = CreateDb();
        var storage = new MemoryAttachmentStorage();
        storage.Put("u/1/stale.bin", "lease-fenced"u8.ToArray());
        var meta = new RecordingMetadataStore();
        var svc = CreateService(db, storage, meta, new DenyListAttachmentContentScanner());

        var winner = new AttachmentScanJob
        {
            AttachmentId = "att-stale",
            ObjectKey = "u/1/stale.bin",
            UserId = 9,
            ContentType = "application/octet-stream",
            OriginalName = "stale.bin",
            SizeBytes = 12,
            Status = AttachmentScanJobStatus.Processing,
            LeaseOwner = "winner",
            LeaseToken = "winner-token",
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        db.AttachmentScanJobs.Add(winner);
        await db.SaveChangesAsync();

        var staleClaim = new AttachmentScanJob
        {
            Id = winner.Id,
            AttachmentId = winner.AttachmentId,
            ObjectKey = winner.ObjectKey,
            UserId = winner.UserId,
            ContentType = winner.ContentType,
            OriginalName = winner.OriginalName,
            SizeBytes = winner.SizeBytes,
            Status = AttachmentScanJobStatus.Processing,
            LeaseOwner = "stale",
            LeaseToken = "stale-token",
        };

        Assert.Equal(
            AttachmentScanProcessResult.LeaseLost,
            await svc.ProcessClaimedJobAsync(staleClaim));
        Assert.Empty(meta.Confirmed);
        Assert.Empty(meta.Rejected);
        Assert.Empty(await db.AttachmentScanProjections.ToListAsync());
    }

    [Fact]
    public async Task ProjectionFailure_RetriesWithoutRescanning()
    {
        await using var db = CreateDb();
        var storage = new MemoryAttachmentStorage();
        storage.Put("u/1/projection.bin", "projection-retry"u8.ToArray());
        var meta = new RecordingMetadataStore { RemainingConfirmFailures = 1 };
        var scanner = new CountingAllowScanner();
        var svc = CreateService(db, storage, meta, scanner);

        await svc.EnqueueAsync("att-projection", 9, "u/1/projection.bin", "application/octet-stream", "projection.bin", 16);

        Assert.Equal(0, await svc.ProcessDueAsync());
        Assert.Equal(1, scanner.Calls);
        var staged = await db.AttachmentScanJobs.SingleAsync();
        Assert.Equal(AttachmentScanJobStatus.Finalizing, staged.Status);
        var retry = await db.AttachmentScanProjections.SingleAsync();
        Assert.Equal(AttachmentScanProjectionStatus.Pending, retry.Status);
        Assert.Equal(1, retry.AttemptCount);

        retry.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        Assert.Equal(1, await svc.ProcessDueAsync());
        Assert.Equal(1, scanner.Calls);
        Assert.Equal(AttachmentScanJobStatus.Done, (await db.AttachmentScanJobs.SingleAsync()).Status);
        Assert.Equal(AttachmentScanProjectionStatus.Done, (await db.AttachmentScanProjections.SingleAsync()).Status);
        Assert.Contains("att-projection", meta.Confirmed);
    }

    [Fact]
    public async Task PermanentDeny_NoRetry_MarksRejected()
    {
        await using var db = CreateDb();
        var storage = new MemoryAttachmentStorage();
        storage.Put("u/1/bad.exe", "not-exe-body"u8.ToArray());
        var meta = new RecordingMetadataStore();
        var svc = CreateService(db, storage, meta, new DenyListAttachmentContentScanner());

        await svc.EnqueueAsync("att-bad", 3, "u/1/bad.exe", "application/octet-stream", "bad.exe", 12);
        Assert.Equal(1, await svc.ProcessDueAsync());

        var done = await db.AttachmentScanJobs.SingleAsync();
        Assert.Equal(AttachmentScanJobStatus.Done, done.Status);
        Assert.Equal(0, done.AttemptCount);
        Assert.Contains("att-bad", meta.Rejected);
        Assert.Empty(meta.Confirmed);
    }

    private static AttachmentScanService CreateService(
        UserDbContext db,
        IAttachmentStorage storage,
        IAttachmentMetadataStore meta,
        IAttachmentContentScanner scanner)
    {
        var options = Options.Create(new AttachmentStorageOptions
        {
            MaxBytes = 1024 * 1024,
            MaxScanAttempts = 10,
            ScanBackoffSeconds = 1,
            ScanBatchSize = 50,
            AllowedContentTypes = ["application/octet-stream", "image/png"],
        });
        var projection = new AttachmentScanProjectionService(
            db,
            meta,
            storage,
            new RecordingBlobDeleteService(),
            options,
            NullLogger<AttachmentScanProjectionService>.Instance);
        return new AttachmentScanService(
            db,
            storage,
            scanner,
            options,
            NullLogger<AttachmentScanService>.Instance,
            projection);
    }

    private static UserDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new UserDbContext(options);
    }

    private sealed class CountingAllowScanner : IAttachmentContentScanner
    {
        public int Calls { get; private set; }

        public Task<AttachmentContentScanResult> ScanAsync(
            Stream content,
            string? sniffedContentType,
            string? originalName,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(AttachmentContentScanResult.Allow());
        }
    }

    private sealed class FlakyScanner(int failTransientUntil) : IAttachmentContentScanner
    {
        private int _calls;

        public Task<AttachmentContentScanResult> ScanAsync(
            Stream content,
            string? sniffedContentType,
            string? originalName,
            CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls <= failTransientUntil)
                return Task.FromResult(AttachmentContentScanResult.TransientFail("scanner_overloaded"));
            return Task.FromResult(AttachmentContentScanResult.Allow());
        }
    }

    private sealed class MemoryAttachmentStorage : IAttachmentStorage
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        public void Put(string key, byte[] bytes) => _files[key] = bytes;

        public bool IsAllowedContentType(string contentType) => true;
        public long MaxBytes => 1024 * 1024;

        public Task<(string AttachmentId, string ObjectKey, string Ticket, string UploadUrl, string PublicUrl, DateTimeOffset ExpiresAt)>
            CreateUploadTicketAsync(
                long userId, string contentType, long contentLength,
                string? originalName = null, string? clientAttachmentId = null,
                CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? AttachmentId, long SizeBytes, string? Sha256Hex, string? Error)>
            StoreAsync(long userId, string ticket, Stream content, string contentType, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? AttachmentId, string? ContentType, long SizeBytes, string? OriginalName, string? Error)>
            ConfirmObjectAsync(
                long userId, string objectKey, string? ticket = null, string? attachmentId = null,
                CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public string? TryResolveLocalPhysicalPath(string objectKey) => null;

        public Task<AttachmentReadResult?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            if (!_files.TryGetValue(objectKey, out var bytes))
                return Task.FromResult<AttachmentReadResult?>(null);
            return Task.FromResult<AttachmentReadResult?>(
                new AttachmentReadResult(new MemoryStream(bytes), "application/octet-stream", bytes.Length, Path.GetFileName(objectKey)));
        }

        public Task<AttachmentSignedUrl?> CreateSignedDownloadUrlAsync(
            string objectKey, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult<AttachmentSignedUrl?>(null);

        public Task DeleteAsync(string objectKeyOrUrl, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task TryDeleteAsync(string? objectKeyOrUrl, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingBlobDeleteService : IAttachmentBlobDeleteService
    {
        public Task EnqueueAsync(
            IEnumerable<string> objectKeys,
            long? userId = null,
            string? attachmentId = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnqueueAsync(
            IEnumerable<(string ObjectKey, string? AttachmentId)> items,
            long? userId = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> ProcessDueAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class RecordingMetadataStore : IAttachmentMetadataStore
    {
        public List<string> Confirmed { get; } = [];
        public List<string> Rejected { get; } = [];
        public List<string> UploadedHashes { get; } = [];
        public int RemainingConfirmFailures { get; set; }
        public bool IsAvailable => true;
        public string UnavailableReason => "";

        public Task InsertTicketedAsync(
            string attachmentId, long uploaderUserId, string objectKey, string? publicUrl,
            string contentType, long sizeBytes, string? originalName, string? clientAttachmentId = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ConfirmAsync(
            string attachmentId, long uploaderUserId, string objectKey, string? publicUrl,
            string contentType, long sizeBytes, string? originalName = null,
            CancellationToken cancellationToken = default)
        {
            if (RemainingConfirmFailures > 0)
            {
                RemainingConfirmFailures--;
                throw new InvalidOperationException("realtime_unavailable");
            }

            Confirmed.Add(attachmentId);
            return Task.CompletedTask;
        }

        public Task MarkUploadedScanningAsync(
            string attachmentId, long uploaderUserId, long sizeBytes, string? sha256Hex = null,
            CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrWhiteSpace(sha256Hex))
                UploadedHashes.Add(sha256Hex);
            return Task.CompletedTask;
        }

        public Task MarkRejectedAsync(
            string attachmentId, long uploaderUserId, string? reason = null,
            CancellationToken cancellationToken = default)
        {
            Rejected.Add(attachmentId);
            return Task.CompletedTask;
        }

        public Task<Core.Models.Attachment.AttachmentDownloadAccess> ResolveDownloadAccessAsync(
            string attachmentId, long userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new Core.Models.Attachment.AttachmentDownloadAccess(
                attachmentId, "", "application/octet-stream", null,
                Core.Models.Attachment.AttachmentDownloadDecision.NotFound));

        public Task<IReadOnlyList<AttachmentRecord>> ListForExportAsync(
            long userId, int maxRows = 50_000, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AttachmentRecord>>([]);

        public Task<IReadOnlyList<string>> ListObjectKeysForUserAsync(
            long uploaderUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlySet<string>> ListActiveObjectKeysAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task MarkAbandonedAsync(IReadOnlyList<string> attachmentIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task MarkAbandonedByUploaderAsync(long uploaderUserId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string?> TryAbandonUnboundByUploaderAsync(
            string attachmentId, long uploaderUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<AttachmentAbandonBatchItem>> AbandonAgedUnboundAsync(
            TimeSpan maxAge,
            int batchSize,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AttachmentAbandonBatchItem>>([]);

        public Task<AttachmentOpsOrphanQueryResult> QueryOpsOrphansAsync(
            TimeSpan orphanAge,
            TimeSpan stuckScanningAge,
            int sampleLimit,
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
}
