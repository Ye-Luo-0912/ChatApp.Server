namespace Core.Interfaces;

/// <summary>头像对象存储预签名 / 本地上传票。</summary>
public interface IAvatarStorage
{
    bool IsAllowedContentType(string contentType);
    long MaxBytes { get; }

    Task<(string ObjectKey, string Ticket, string UploadUrl, string PublicUrl, DateTimeOffset ExpiresAt)>
        CreateUploadTicketAsync(long userId, string contentType, long contentLength, CancellationToken cancellationToken = default);

    Task<(bool Ok, string? PublicUrl, string? Error)> StoreAsync(
        long userId, string ticket, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>确认对象：校验归属、存在性（S3 还会解码重编码）。</summary>
    Task<(bool Ok, string? PublicUrl, string? Error)> ConfirmObjectAsync(
        long userId, string objectKey, CancellationToken cancellationToken = default);

    Task TryDeleteAsync(string? objectKeyOrUrl, CancellationToken cancellationToken = default);
}
