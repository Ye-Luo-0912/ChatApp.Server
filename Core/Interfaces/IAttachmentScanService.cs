using Core.Models.Export;

namespace Core.Interfaces;

/// <summary>附件内容扫描入队与带退避重试。</summary>
public interface IAttachmentScanService
{
    Task EnqueueAsync(
        string attachmentId,
        long userId,
        string objectKey,
        string? contentType,
        string? originalName,
        long sizeBytes,
        CancellationToken cancellationToken = default);

    /// <summary>处理到期 Pending 扫描作业；返回本轮完成数（Confirmed 或永久 Rejected）。</summary>
    Task<int> ProcessDueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 原子领取到期作业（带 LeaseToken fencing）。P0-5.2：Worker 用此方法只领取当前可处理的数量，
    /// 避免一次领取大批量后串行处理导致后续作业租约过期。
    /// 返回的快照已脱离 DbContext，调用方应在独立作用域中用 <see cref="ProcessClaimedJobAsync"/> 处理。
    /// </summary>
    Task<IReadOnlyList<AttachmentScanJob>> ClaimDueJobsAsync(
        int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// 处理单个已领取作业。终态更新匹配 Id+Status+LeaseOwner+LeaseToken；租约已易主时返回 false 且不改动。
    /// </summary>
    /// <returns>是否进入终态（Confirmed/Rejected/Done）；瞬时失败重试或租约丢失返回 false。</returns>
    Task<bool> ProcessClaimedJobAsync(
        AttachmentScanJob claimed, CancellationToken cancellationToken = default);

    /// <summary>
    /// 续租：仅当本实例仍持有该 LeaseToken 时延长 LeaseExpiresAt。处理大文件期间由 Worker 心跳调用。
    /// </summary>
    Task<int> RenewLeaseAsync(
        long jobId, string leaseOwner, string leaseToken, CancellationToken cancellationToken = default);
}
