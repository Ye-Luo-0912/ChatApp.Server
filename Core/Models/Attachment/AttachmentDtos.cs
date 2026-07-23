namespace Core.Models.Attachment;

public sealed class AttachmentPresignRequest
{
    public string ContentType { get; set; } = string.Empty;
    public long ContentLength { get; set; }
    public string? OriginalName { get; set; }
    public string? ClientAttachmentId { get; set; }
}

public sealed class AttachmentPresignResponse
{
    public string AttachmentId { get; init; } = string.Empty;
    public string UploadUrl { get; init; } = string.Empty;

    /// <summary>鉴权下载路径，例如 /api/attachments/{id}/download。不再返回永久 PublicUrl。</summary>
    public string DownloadPath { get; init; } = string.Empty;

    /// <summary>上传确认所需的对象键（内部存储键，非公开 URL）。</summary>
    public string ObjectKey { get; init; } = string.Empty;

    public string Ticket { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>已废弃：永久公开 URL 会泄漏未鉴权读。保留属性仅为旧客户端兼容，始终为空。</summary>
    [Obsolete("Use DownloadPath. Permanent PublicUrl is no longer returned.")]
    public string PublicUrl { get; init; } = string.Empty;
}

public sealed class ConfirmAttachmentRequest
{
    public string ObjectKey { get; set; } = string.Empty;

    /// <summary>S3 预签名上传后确认时必填；Local 上传后可选。</summary>
    public string? Ticket { get; set; }

    public string? AttachmentId { get; set; }
}

public sealed class ConfirmAttachmentResponse
{
    public string AttachmentId { get; init; } = string.Empty;

    /// <summary>鉴权下载路径，例如 /api/attachments/{id}/download。</summary>
    public string DownloadPath { get; init; } = string.Empty;

    /// <summary>内部对象键（非公开 URL）。</summary>
    public string ObjectKey { get; init; } = string.Empty;

    /// <summary>已废弃：永久公开 URL。始终为空。</summary>
    [Obsolete("Use DownloadPath. Permanent PublicUrl is no longer returned.")]
    public string PublicUrl { get; init; } = string.Empty;
}

public sealed class AttachmentSignedDownloadResponse
{
    public string Url { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>下载授权解析结果。</summary>
public sealed record AttachmentDownloadAccess(
    string AttachmentId,
    string ObjectKey,
    string ContentType,
    string? OriginalName,
    AttachmentDownloadDecision Decision);

public enum AttachmentDownloadDecision
{
    Allowed = 0,
    NotFound = 1,
    Forbidden = 2,
    Unavailable = 3,
    /// <summary>仍在 Uploaded/Scanning；客户端应稍后重试（HTTP 409）。</summary>
    NotReady = 4,
}

public static class AttachmentApiPaths
{
    public static string DownloadPath(string attachmentId) =>
        $"/api/attachments/{Uri.EscapeDataString(attachmentId)}/download";
}
