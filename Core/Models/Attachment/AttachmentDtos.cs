using System.Text.Json.Serialization;
using Core.Models.Export;

namespace Core.Models.Attachment;

public sealed class AttachmentPresignRequest
{
    public string ContentType { get; set; } = string.Empty;
    public long ContentLength { get; set; }
    public string? OriginalName { get; set; }
    public string? ClientAttachmentId { get; set; }

    /// <summary>
    /// 文件 SHA-256（64 位小写 hex）。合法时服务端先做内容寻址去重查找：
    /// 命中已确认/已绑定内容则返回 <see cref="AttachmentPresignResponse.Deduplicated"/>，
    /// 客户端跳过上传直接确认。
    /// </summary>
    public string? Sha256 { get; set; }
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

    /// <summary>
    /// True 表示服务端已持有相同 SHA-256 内容：客户端必须跳过 PUT 上传，
    /// 直接以本响应的 ObjectKey/Ticket/AttachmentId 调用确认。
    /// </summary>
    public bool Deduplicated { get; init; }

    /// <summary>
    /// S3 presigned PUT headers (Content-Type, SSE and initial quarantine tag).
    /// Clients must send these headers unchanged with the PUT request.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? UploadHeaders { get; init; }

    /// <summary>已废弃：永久公开 URL 会泄漏未鉴权读。保留属性仅为旧客户端兼容，始终为空。</summary>
    [Obsolete("Use DownloadPath. Permanent PublicUrl is no longer returned.")]
    public string PublicUrl { get; init; } = string.Empty;
}

/// <summary>上传票在元数据侧的原子预留结果。</summary>
public enum AttachmentUploadReservationStatus
{
    Unknown = 0,
    Reserved = 1,
    UnconfirmedObjectLimitExceeded = 2,
    StorageBytesLimitExceeded = 3,
    MetadataUnavailable = 4,
}

/// <summary>预签名编排结果；配额拒绝属于预期业务结果，不使用异常控制流。</summary>
public readonly record struct AttachmentPresignResult(
    AttachmentUploadReservationStatus Status,
    AttachmentPresignResponse? Response);

public sealed class ConfirmAttachmentRequest
{
    public string ObjectKey { get; set; } = string.Empty;

    /// <summary>S3 预签名上传后确认时必填；Local 上传后可选。</summary>
    public string? Ticket { get; set; }

    public string? AttachmentId { get; set; }
}

public sealed class ConfirmAttachmentResponse
{
    public long SagaId { get; init; }
    public string AttachmentId { get; init; } = string.Empty;

    /// <summary>鉴权下载路径，例如 /api/attachments/{id}/download。</summary>
    public string DownloadPath { get; init; } = string.Empty;

    /// <summary>内部对象键（非公开 URL）。</summary>
    public string ObjectKey { get; init; } = string.Empty;

    /// <summary>
    /// Confirm 后内容扫描异步进行时为 <c>Scanning</c>；已确认则为 <c>Confirmed</c>。
    /// </summary>
    public string Status { get; init; } = "Scanning";

    /// <summary>Durable Server-side confirmation Saga state.</summary>
    public string SagaStatus { get; init; } = "Requested";

    /// <summary>已废弃：永久公开 URL。始终为空。</summary>
    [Obsolete("Use DownloadPath. Permanent PublicUrl is no longer returned.")]
    public string PublicUrl { get; init; } = string.Empty;
}

/// <summary>附件生命周期查询结果；仅返回上传者可见的元数据。</summary>
public sealed record AttachmentLifecycleStatusDto(
    string AttachmentId,
    AttachmentStatus Status,
    string StatusName,
    long SizeBytes,
    string ContentType,
    string? OriginalName,
    long CreatedAtMs,
    long? ConfirmedAtMs,
    long? BoundAtMs,
    string DownloadPath);

public sealed class AttachmentSignedDownloadResponse
{
    public string Url { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// 短时下载票：绑定 userId+attachmentId，Redis TTL 内单次消费。
/// 服务端通过鉴权并接受下载响应即消费；传输中断需重新签发。
/// </summary>
public sealed class AttachmentDownloadTicketResponse
{
    public string AttachmentId { get; init; } = string.Empty;
    public string Ticket { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>带 ticket 查询参数的下载路径。</summary>
    public string DownloadUrl { get; init; } = string.Empty;
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
    /// <summary>下载票无效、过期、已消费或与用户/附件不匹配。</summary>
    InvalidTicket = 5,
}

public static class AttachmentApiPaths
{
    public static string DownloadPath(string attachmentId) =>
        $"/api/attachments/{Uri.EscapeDataString(attachmentId)}/download";

    public static string DownloadPathWithTicket(string attachmentId, string ticket) =>
        $"{DownloadPath(attachmentId)}?ticket={Uri.EscapeDataString(ticket)}";
}
