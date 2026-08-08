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

    /// <summary>为头像候选/旧对象写入同一套 durable blob 删除墓碑。</summary>
    Task EnqueueAvatarAsync(
        IEnumerable<string> objectKeys,
        long? userId = null,
        CancellationToken cancellationToken = default)
        => EnqueueAsync(objectKeys, userId, attachmentId: null, cancellationToken);

    /// <summary>
    /// Registers a final avatar as a crash-recoverable candidate. The
    /// implementation must not delete it before the publication grace period
    /// expires; the avatar DB transaction calls PublishAvatarCandidatesAsync
    /// when it references the object.
    /// </summary>
    Task EnqueueAvatarCandidatesAsync(
        IEnumerable<string> objectKeys,
        long? userId = null,
        CancellationToken cancellationToken = default)
        => EnqueueAvatarAsync(objectKeys, userId, cancellationToken);

    /// <summary>Marks candidate rows published in the caller's DB transaction.</summary>
    Task PublishAvatarCandidatesAsync(
        IEnumerable<string> objectKeys,
        long? userId = null,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>Releases candidates after the owning DB transaction rolls back.</summary>
    Task ReleaseAvatarCandidatesAsync(
        IEnumerable<string> objectKeys,
        long? userId = null,
        CancellationToken cancellationToken = default)
        => EnqueueAvatarAsync(objectKeys, userId, cancellationToken);

    /// <summary>处理到期 Pending 墓碑；返回本轮成功删除数。</summary>
    Task<int> ProcessDueAsync(CancellationToken cancellationToken = default);
}
