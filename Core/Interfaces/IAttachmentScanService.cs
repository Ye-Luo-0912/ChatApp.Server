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
}
