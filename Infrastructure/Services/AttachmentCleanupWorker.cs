using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Core.Interfaces;
using Core.Settings;
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
    ILogger<AttachmentCleanupWorker> logger) : BackgroundService
{
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
        await using var scope = scopeFactory.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAttachmentBlobDeleteService>();
        var deleted = await svc.ProcessDueAsync(ct).ConfigureAwait(false);
        if (deleted > 0)
            logger.LogInformation("附件墓碑删除成功 {Count} 个对象", deleted);
    }

    private async Task CleanupS3Async(AttachmentStorageOptions opts, TimeSpan maxAge, CancellationToken ct)
    {
        var (activeKeys, metadataAvailable) = await LoadActiveKeysAsync(ct).ConfigureAwait(false);

        var config = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(opts.S3Region ?? "us-east-1"),
            ForcePathStyle = true,
        };
        if (!string.IsNullOrWhiteSpace(opts.S3Endpoint))
            config.ServiceURL = opts.S3Endpoint;

        using var s3 = new AmazonS3Client(
            new BasicAWSCredentials(opts.S3AccessKey, opts.S3SecretKey),
            config);

        var cutoff = DateTime.UtcNow - maxAge;
        string? token = null;
        var deleted = 0;
        do
        {
            var list = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = opts.S3Bucket,
                Prefix = "attachments/",
                ContinuationToken = token,
            }, ct).ConfigureAwait(false);

            foreach (var obj in list.S3Objects)
            {
                if (obj.LastModified.ToUniversalTime() > cutoff)
                    continue;

                if (!metadataAvailable)
                {
                    // 无元数据时仅清理临时 .bin，避免误删正式对象。
                    if (!obj.Key.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                else if (activeKeys.Contains(obj.Key))
                {
                    continue;
                }

                try
                {
                    await s3.DeleteObjectAsync(opts.S3Bucket, obj.Key, ct).ConfigureAwait(false);
                    deleted++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "删除过期附件失败 Key={Key}", obj.Key);
                }
            }

            token = list.IsTruncated ? list.NextContinuationToken : null;
        } while (token is not null && !ct.IsCancellationRequested);

        if (deleted > 0)
            logger.LogInformation("已清理 {Count} 个 S3 过期/孤儿附件对象", deleted);
    }

    private async Task CleanupLocalAsync(AttachmentStorageOptions opts, TimeSpan maxAge, CancellationToken ct)
    {
        var root = Path.GetFullPath(opts.LocalRootPath);
        if (!Directory.Exists(root)) return;

        var (activeKeys, metadataAvailable) = await LoadActiveKeysAsync(ct).ConfigureAwait(false);
        var cutoff = DateTime.UtcNow - maxAge;
        var deleted = 0;
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var pattern = metadataAvailable ? "*" : "*.bin";
        foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (File.GetLastWriteTimeUtc(file) > cutoff) continue;

                var relative = Path.GetRelativePath(rootWithSep, file).Replace('\\', '/');
                if (metadataAvailable && activeKeys.Contains(relative))
                    continue;

                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "删除本地过期/孤儿附件失败 Path={Path}", file);
            }
        }

        if (deleted > 0)
            logger.LogInformation("已清理 {Count} 个本地过期/孤儿附件文件", deleted);
    }

    private async Task<(IReadOnlySet<string> Keys, bool Available)> LoadActiveKeysAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var metadata = scope.ServiceProvider.GetRequiredService<IAttachmentMetadataStore>();
        if (!metadata.IsAvailable)
            return (new HashSet<string>(StringComparer.Ordinal), false);
        var keys = await metadata.ListActiveObjectKeysAsync(ct).ConfigureAwait(false);
        return (keys, true);
    }
}
