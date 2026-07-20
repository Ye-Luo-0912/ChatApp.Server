using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>定期清理 S3 临时 .bin 对象与本地过期临时文件。</summary>
public sealed class AvatarCleanupWorker(
    IOptions<AvatarStorageOptions> options,
    ILogger<AvatarCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opts = options.Value;
                var maxAge = TimeSpan.FromMinutes(Math.Max(30, opts.TicketMinutes * 4));

                if (string.Equals(opts.Provider, "S3", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(opts.S3Bucket))
                {
                    await CleanupS3Async(opts, maxAge, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    CleanupLocal(opts, maxAge);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "头像临时对象清理失败");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task CleanupS3Async(AvatarStorageOptions opts, TimeSpan maxAge, CancellationToken ct)
    {
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
                Prefix = "avatars/",
                ContinuationToken = token,
            }, ct).ConfigureAwait(false);

            foreach (var obj in list.S3Objects)
            {
                if (!obj.Key.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (obj.LastModified.ToUniversalTime() > cutoff)
                    continue;

                try
                {
                    await s3.DeleteObjectAsync(opts.S3Bucket, obj.Key, ct).ConfigureAwait(false);
                    deleted++;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "删除临时头像失败 Key={Key}", obj.Key);
                }
            }

            token = list.IsTruncated ? list.NextContinuationToken : null;
        } while (token is not null && !ct.IsCancellationRequested);

        if (deleted > 0)
            logger.LogInformation("已清理 {Count} 个 S3 临时头像对象", deleted);
    }

    private void CleanupLocal(AvatarStorageOptions opts, TimeSpan maxAge)
    {
        var root = Path.GetFullPath(opts.LocalRootPath);
        if (!Directory.Exists(root)) return;

        var cutoff = DateTime.UtcNow - maxAge;
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "删除本地临时头像失败 Path={Path}", file);
            }
        }

        if (deleted > 0)
            logger.LogInformation("已清理 {Count} 个本地临时头像文件", deleted);
    }
}
