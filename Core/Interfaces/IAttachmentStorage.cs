namespace Core.Interfaces;

/// <summary>正式附件对象存储：预签名 / 本地上传票；鉴权下载；可失败删除。</summary>
public interface IAttachmentStorage
{
    bool IsAllowedContentType(string contentType);
    long MaxBytes { get; }

    Task<(string AttachmentId, string ObjectKey, string Ticket, string UploadUrl, string PublicUrl, DateTimeOffset ExpiresAt)>
        CreateUploadTicketAsync(
            long userId,
            string contentType,
            long contentLength,
            string? originalName = null,
            string? clientAttachmentId = null,
            CancellationToken cancellationToken = default);

    /// <summary>
    /// 撤销尚未返回给客户端的上传票。用于元数据预留失败后的编排补偿；幂等。
    /// </summary>
    Task CancelUploadTicketAsync(
        string ticket,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 将请求体流式写入存储。Local 落临时 <c>.uploading</c> 后原子改名；不整文件进内存。
    /// 成功时返回实际字节数与 SHA-256（hex）。
    /// </summary>
    Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? AttachmentId, long SizeBytes, string? Sha256Hex, string? Error)> StoreAsync(
        long userId,
        string ticket,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>确认暂存对象存在；扫描通过后存储实现可将其提升到不可变最终键。</summary>
    Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? AttachmentId, string? ContentType, long SizeBytes, string? OriginalName, string? Error)>
        ConfirmObjectAsync(
            long userId,
            string objectKey,
            string? ticket = null,
            string? attachmentId = null,
            CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析本地磁盘绝对路径（须在存储根下且文件存在）。S3 实现返回 null。
    /// 供下载走 <c>PhysicalFileResult</c>，避免用户态再拷一份流。
    /// </summary>
    string? TryResolveLocalPhysicalPath(string objectKey);

    /// <summary>
    /// 打开本地对象流。S3 实现返回 null（应改用签名 URL）。
    /// 调用方负责 Dispose Stream。
    /// </summary>
    Task<AttachmentReadResult?> OpenReadAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    /// <summary>S3/兼容存储短时签名 GET；Local 返回 null。</summary>
    Task<AttachmentSignedUrl?> CreateSignedDownloadUrlAsync(
        string objectKey,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除对象；对象不存在视为成功。失败时抛出（供墓碑 Worker 记录 LastError）。
    /// </summary>
    Task DeleteAsync(string objectKeyOrUrl, CancellationToken cancellationToken = default);

    /// <summary>尽力删除；失败仅记日志。新代码优先 <see cref="DeleteAsync"/>。</summary>
    Task TryDeleteAsync(string? objectKeyOrUrl, CancellationToken cancellationToken = default);
}

public sealed record AttachmentReadResult(
    Stream Content,
    string ContentType,
    long? Length,
    string? FileName,
    string? EntityTag = null);

public sealed record AttachmentSignedUrl(string Url, DateTimeOffset ExpiresAt);
