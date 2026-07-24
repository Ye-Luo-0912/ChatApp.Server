using Core.Interfaces;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// 将过期未绑定 Ticketed/Confirmed 标为 Abandoned，并入队 blob 删除墓碑。
/// </summary>
public sealed class AttachmentAbandonedAgeSweeper(
    IAttachmentMetadataStore metadata,
    IAttachmentBlobDeleteService blobDeletes,
    IOptions<AttachmentStorageOptions> options,
    ILogger<AttachmentAbandonedAgeSweeper> logger)
{
    public static TimeSpan ResolveMaxAge(AttachmentStorageOptions opts)
    {
        if (opts.AbandonedUnboundAgeMinutes > 0)
            return TimeSpan.FromMinutes(opts.AbandonedUnboundAgeMinutes);
        return TimeSpan.FromMinutes(Math.Max(30, opts.TicketMinutes * 4));
    }

    /// <summary>执行一轮清扫；返回本轮放弃条数。</summary>
    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        if (!opts.AbandonedUnboundEnabled || !metadata.IsAvailable)
            return 0;

        var maxAge = ResolveMaxAge(opts);
        var batchSize = Math.Clamp(opts.AbandonedUnboundBatchSize, 1, 200);
        var abandoned = await metadata
            .AbandonAgedUnboundAsync(maxAge, batchSize, cancellationToken)
            .ConfigureAwait(false);
        if (abandoned.Count == 0)
            return 0;

        try
        {
            await blobDeletes
                .EnqueueAsync(
                    abandoned.Select(a => (a.ObjectKey, (string?)a.AttachmentId)),
                    userId: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "年龄清扫已 Abandoned {Count} 条，但 blob 删除入队失败",
                abandoned.Count);
            throw;
        }

        logger.LogInformation(
            "附件年龄清扫：Abandoned {Count} 条未绑定 Ticketed/Confirmed（maxAge={MaxAgeMinutes}m）",
            abandoned.Count,
            (int)maxAge.TotalMinutes);
        return abandoned.Count;
    }
}
