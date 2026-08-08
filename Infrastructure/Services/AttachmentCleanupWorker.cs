using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// 处理附件 blob 删除墓碑；并清理过期临时/孤儿对象（任意扩展名，跳过 Confirmed/Bound）。
/// </summary>
public sealed class AttachmentCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AttachmentStorageOptions> options,
    IOptions<WorkerConcurrencyOptions> workerConcurrencyOptions,
    ILeasedJobStore<AttachmentBlobDeleteJob> blobDeleteStore,
    LeasedJobExecutor<AttachmentBlobDeleteJob> blobDeleteExecutor,
    IAttachmentStorage attachmentStorage,
    IAvatarStorage avatarStorage,
    ILogger<AttachmentCleanupWorker> logger) : BackgroundService
{
    private const string WorkerName = "attachment_blob_delete";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后稍等再跑，避免与迁移竞态。
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAgedUnboundAsync(stoppingToken).ConfigureAwait(false);
                await ProcessDeleteJobsAsync(stoppingToken).ConfigureAwait(false);

                var opts = options.Value;
                var maxAge = TimeSpan.FromMinutes(Math.Max(30, opts.TicketMinutes * 4));

                if (string.Equals(opts.Provider, "S3", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(opts.S3Bucket))
                {
                    await CleanupS3Async(opts, maxAge, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    await CleanupLocalAsync(opts, maxAge, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "附件清理失败");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SweepAgedUnboundAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sweeper = scope.ServiceProvider.GetRequiredService<AttachmentAbandonedAgeSweeper>();
        await sweeper.SweepOnceAsync(ct).ConfigureAwait(false);
    }

    private async Task ProcessDeleteJobsAsync(CancellationToken ct)
    {
        var deleted = await blobDeleteExecutor.DrainAsync(
                WorkerName,
                Math.Max(1, workerConcurrencyOptions.Value.AttachmentBlobDelete),
                TimeSpan.FromMinutes(AttachmentBlobDeleteService.LeaseMinutes),
                blobDeleteStore,
                (job, cancellationToken) =>
                    string.Equals(
                        job.StorageKind,
                        AttachmentBlobDeleteStorageKind.Avatar,
                        StringComparison.Ordinal)
                        ? avatarStorage.TryDeleteAsync(job.ObjectKey, cancellationToken)
                        : attachmentStorage.DeleteAsync(job.ObjectKey, cancellationToken),
                job => job.AttemptCount + 1 >= Math.Max(1, options.Value.MaxDeleteAttempts),
                ct)
            .ConfigureAwait(false);
        if (deleted > 0)
            logger.LogInformation("附件墓碑删除成功 {Count} 个对象", deleted);
    }

    private async Task CleanupS3Async(AttachmentStorageOptions opts, TimeSpan maxAge, CancellationToken ct)
    {
        // S3 is intentionally not globally listed here. Client-writable
        // staging/quarantine objects are covered by bucket Lifecycle rules;
        // confirmed/abandoned objects are removed through durable Server DB
        // tombstones. The cleanup loop therefore stays O(expired candidates)
        // instead of O(all attachments) on every five-minute pass.
        _ = opts;
        _ = maxAge;
        _ = ct;
        logger.LogDebug("S3 附件临时前缀由 Lifecycle 与 durable tombstone 清理");
    }

    private async Task CleanupLocalAsync(AttachmentStorageOptions opts, TimeSpan maxAge, CancellationToken ct)
    {
        var root = Path.GetFullPath(opts.LocalRootPath);
        if (!Directory.Exists(root)) return;

        var cutoff = DateTime.UtcNow - maxAge;
        var deleted = 0;
        // New transient objects live below lifecycle prefixes. Legacy final
        // objects are deleted by DB tombstones, so this local sweep only walks
        // bounded candidate prefixes and never loads active keys.
        var candidateRoots = new List<string>();
        foreach (var prefix in new[] { "pending", "quarantine", "staging" })
        {
            var candidate = Path.Combine(root, prefix);
            if (Directory.Exists(candidate))
                candidateRoots.Add(candidate);
        }

        foreach (var candidateRoot in candidateRoots)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(candidateRoot))
            {
                deleted += TryDeleteLocalCandidate(candidateRoot, cutoff);
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(candidateRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                deleted += TryDeleteLocalCandidate(file, cutoff);
            }
        }

        if (deleted > 0)
            logger.LogInformation("已清理 {Count} 个本地过期/孤儿附件文件", deleted);
    }

    private int TryDeleteLocalCandidate(string file, DateTime cutoff)
    {
        try
        {
            if (File.GetLastWriteTimeUtc(file) > cutoff)
                return 0;
            File.Delete(file);
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "删除本地过期附件候选失败 Path={Path}", file);
            return 0;
        }
    }
}
