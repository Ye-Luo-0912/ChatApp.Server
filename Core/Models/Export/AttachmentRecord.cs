namespace Core.Models.Export;

/// <summary>realtime.attachments 行（Server 侧读写契约）。</summary>
public sealed record AttachmentRecord(
    string AttachmentId,
    long UploaderUserId,
    string ObjectKey,
    string? PublicUrl,
    string ContentType,
    long SizeBytes,
    string? OriginalName,
    AttachmentStatus Status,
    string? MessageId,
    string? ConversationId,
    string? ClientAttachmentId,
    long CreatedAtMs,
    long? ConfirmedAtMs,
    long? BoundAtMs);
