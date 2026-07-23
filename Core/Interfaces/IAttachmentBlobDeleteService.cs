namespace Core.Interfaces;

/// <summary>附件 blob 删除墓碑入队与重试。</summary>
public interface IAttachmentBlobDeleteService
{
    Task EnqueueAsync(
        IEnumerable<string> objectKeys,
        long? userId = null,
        string? attachmentId = null,
        CancellationToken cancellationToken = default);

    Task EnqueueAsync(
        IEnumerable<(string ObjectKey, string? AttachmentId)> items,
        long? userId = null,
        CancellationToken cancellationToken = default);

    /// <summary>处理到期 Pending 墓碑；返回本轮成功删除数。</summary>
    Task<int> ProcessDueAsync(CancellationToken cancellationToken = default);
}
