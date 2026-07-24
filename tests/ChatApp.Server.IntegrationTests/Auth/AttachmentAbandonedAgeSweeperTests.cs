using Core.Interfaces;
using Core.Models.Attachment;
using Core.Settings;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

public sealed class AttachmentAbandonedAgeSweeperTests
{
    [Fact]
    public async Task SweepOnce_AbandonsAndEnqueuesBlobDeletes()
    {
        var metadata = new ScriptedMetadataStore(
        [
            new AttachmentAbandonBatchItem("att-1", "user/1/a.bin", 11),
            new AttachmentAbandonBatchItem("att-2", "user/2/b.bin", 22),
        ]);
        var deletes = new RecordingBlobDeleteService();
        var opts = Options.Create(new AttachmentStorageOptions
        {
            AbandonedUnboundEnabled = true,
            AbandonedUnboundAgeMinutes = 60,
            AbandonedUnboundBatchSize = 50,
            TicketMinutes = 15,
        });

        var sweeper = new AttachmentAbandonedAgeSweeper(
            metadata,
            deletes,
            opts,
            NullLogger<AttachmentAbandonedAgeSweeper>.Instance);

        var count = await sweeper.SweepOnceAsync();
        Assert.Equal(2, count);
        Assert.Equal(60, (int)metadata.LastMaxAge.TotalMinutes);
        Assert.Equal(50, metadata.LastBatchSize);
        Assert.Equal(2, deletes.Items.Count);
        Assert.Contains(deletes.Items, x => x.ObjectKey == "user/1/a.bin" && x.AttachmentId == "att-1");
        Assert.Contains(deletes.Items, x => x.ObjectKey == "user/2/b.bin" && x.AttachmentId == "att-2");
    }

    [Fact]
    public async Task SweepOnce_Disabled_DoesNothing()
    {
        var metadata = new ScriptedMetadataStore(
            [new AttachmentAbandonBatchItem("att-1", "user/1/a.bin", 11)]);
        var deletes = new RecordingBlobDeleteService();
        var opts = Options.Create(new AttachmentStorageOptions { AbandonedUnboundEnabled = false });

        var sweeper = new AttachmentAbandonedAgeSweeper(
            metadata,
            deletes,
            opts,
            NullLogger<AttachmentAbandonedAgeSweeper>.Instance);

        Assert.Equal(0, await sweeper.SweepOnceAsync());
        Assert.Empty(deletes.Items);
        Assert.False(metadata.Called);
    }

    [Fact]
    public void ResolveMaxAge_FallsBackToTicketHeuristic()
    {
        var age = AttachmentAbandonedAgeSweeper.ResolveMaxAge(new AttachmentStorageOptions
        {
            AbandonedUnboundAgeMinutes = 0,
            TicketMinutes = 15,
        });
        Assert.Equal(TimeSpan.FromMinutes(60), age);
    }

    private sealed class ScriptedMetadataStore(
        IReadOnlyList<AttachmentAbandonBatchItem> items) : IAttachmentMetadataStore
    {
        public bool Called { get; private set; }
        public TimeSpan LastMaxAge { get; private set; }
        public int LastBatchSize { get; private set; }
        public bool IsAvailable => true;
        public string UnavailableReason => "";

        public Task InsertTicketedAsync(
            string attachmentId, long uploaderUserId, string objectKey, string? publicUrl,
            string contentType, long sizeBytes, string? originalName, string? clientAttachmentId = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ConfirmAsync(
            string attachmentId, long uploaderUserId, string objectKey, string? publicUrl,
            string contentType, long sizeBytes, string? originalName = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task MarkUploadedScanningAsync(
            string attachmentId, long uploaderUserId, long sizeBytes, string? sha256Hex = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task MarkRejectedAsync(
            string attachmentId, long uploaderUserId, string? reason = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AttachmentDownloadAccess> ResolveDownloadAccessAsync(
            string attachmentId, long userId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Core.Models.Export.AttachmentRecord>> ListForExportAsync(
            long userId, int maxRows = 50000, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListObjectKeysForUserAsync(
            long uploaderUserId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlySet<string>> ListActiveObjectKeysAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task MarkAbandonedAsync(IReadOnlyList<string> attachmentIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task MarkAbandonedByUploaderAsync(long uploaderUserId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string?> TryAbandonUnboundByUploaderAsync(
            string attachmentId, long uploaderUserId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AttachmentAbandonBatchItem>> AbandonAgedUnboundAsync(
            TimeSpan maxAge, int batchSize, CancellationToken cancellationToken = default)
        {
            Called = true;
            LastMaxAge = maxAge;
            LastBatchSize = batchSize;
            return Task.FromResult(items);
        }

        public Task<AttachmentOpsOrphanQueryResult> QueryOpsOrphansAsync(
            TimeSpan orphanAge, TimeSpan stuckScanningAge, int sampleLimit,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingBlobDeleteService : IAttachmentBlobDeleteService
    {
        public List<(string ObjectKey, string? AttachmentId)> Items { get; } = [];

        public Task EnqueueAsync(
            IEnumerable<string> objectKeys,
            long? userId = null,
            string? attachmentId = null,
            CancellationToken cancellationToken = default)
        {
            foreach (var key in objectKeys)
                Items.Add((key, attachmentId));
            return Task.CompletedTask;
        }

        public Task EnqueueAsync(
            IEnumerable<(string ObjectKey, string? AttachmentId)> items,
            long? userId = null,
            CancellationToken cancellationToken = default)
        {
            Items.AddRange(items);
            return Task.CompletedTask;
        }

        public Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }
}
