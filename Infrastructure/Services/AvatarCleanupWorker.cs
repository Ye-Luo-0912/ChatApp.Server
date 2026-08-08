using Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>定期清理 S3 临时对象与本地 pending 临时文件。</summary>
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
        // Pending avatar candidates are tagged and isolated under the
        // avatars/*/pending/ prefix. S3 Lifecycle owns this cleanup; listing
        // the entire avatars/ prefix here would be O(all confirmed avatars).
        _ = opts;
        _ = maxAge;
        _ = ct;
        logger.LogDebug("S3 头像 pending 对象由 Lifecycle 清理，确认对象由 durable tombstone 删除");
    }

    private void CleanupLocal(AvatarStorageOptions opts, TimeSpan maxAge)
    {
        var root = Path.GetFullPath(opts.LocalRootPath);
        if (!Directory.Exists(root)) return;

        var cutoff = DateTime.UtcNow - maxAge;
        var deleted = 0;
        // Do not walk confirmed avatars.  Each user directory is inspected
        // only for its bounded pending/temporary subdirectory; legacy .bin
        // files directly below the user directory remain safe candidates.
        foreach (var userDirectory in Directory.EnumerateDirectories(root))
        {
            var pendingDirectory = Path.Combine(userDirectory, "pending");
            if (Directory.Exists(pendingDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(
                             pendingDirectory, "*", SearchOption.AllDirectories))
                {
                    deleted += TryDeleteCandidate(file, cutoff);
                }
            }

            foreach (var legacy in Directory.EnumerateFiles(userDirectory, "*.bin", SearchOption.TopDirectoryOnly))
            {
                deleted += TryDeleteCandidate(legacy, cutoff);
            }
        }

        if (deleted > 0)
            logger.LogInformation("已清理 {Count} 个本地临时头像文件", deleted);
    }

    private int TryDeleteCandidate(string file, DateTime cutoff)
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
            logger.LogDebug(ex, "删除本地临时头像失败 Path={Path}", file);
            return 0;
        }
    }
}
