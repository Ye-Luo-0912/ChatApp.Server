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
        => new(
            db,
            storage,
            meta,
            scanner,
            Options.Create(new AttachmentStorageOptions
            {
                MaxBytes = 1024 * 1024,
                MaxScanAttempts = 10,
                ScanBackoffSeconds = 1,
                ScanBatchSize = 50,
                AllowedContentTypes = ["application/octet-stream", "image/png"],
            }),
            NullLogger<AttachmentScanService>.Instance);

    private static UserDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new UserDbContext(options);
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

    private sealed class RecordingMetadataStore : IAttachmentMetadataStore
    {
        public List<string> Confirmed { get; } = [];
        public List<string> Rejected { get; } = [];
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
            Confirmed.Add(attachmentId);
            return Task.CompletedTask;
        }

        public Task MarkUploadedScanningAsync(
            string attachmentId, long uploaderUserId, long sizeBytes, string? sha256Hex = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

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
