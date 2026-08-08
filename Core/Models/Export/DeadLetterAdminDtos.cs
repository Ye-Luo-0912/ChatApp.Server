namespace Core.Models.Export;

public static class DeadLetterQueueNames
{
    public const string EmailOutbox = "email_outbox";
    public const string NotificationOutbox = "notification_outbox";
    public const string AttachmentScan = "attachment_scan";
    public const string AttachmentProjection = "attachment_projection";
    public const string AttachmentConfirm = "attachment_confirm";
    public const string AttachmentBlobDelete = "attachment_blob_delete";
    public const string DataExport = "data_export";
    public const string DataExportBlobDelete = "data_export_blob_delete";
    public const string ModerationRevocation = "moderation_revocation";
    public const string LoginAudit = "login_audit";
    public const string LoginRisk = "login_risk";
    public const string AccountDeletion = "account_deletion";
    public const string AccountCleanupSaga = "account_cleanup_saga";
    public const string RealtimeOutbox = "realtime_outbox";

    public static readonly IReadOnlyList<string> All =
    [
        EmailOutbox,
        NotificationOutbox,
        AttachmentScan,
        AttachmentProjection,
        AttachmentConfirm,
        AttachmentBlobDelete,
        DataExport,
        DataExportBlobDelete,
        ModerationRevocation,
        LoginAudit,
        LoginRisk,
        AccountDeletion,
        AccountCleanupSaga,
        RealtimeOutbox,
    ];
}

public sealed record DeadLetterItemDto(
    string Queue,
    string JobId,
    long? UserId,
    string Status,
    int AttemptCount,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? NextAttemptAt,
    string? LastError,
    string? Detail,
    string? ResolutionAction,
    DateTimeOffset? ResolutionAt);

public sealed record DeadLetterPage(
    IReadOnlyList<DeadLetterItemDto> Items,
    string? Queue,
    int Offset,
    int Limit,
    bool HasMore);

public sealed record DeadLetterActionResult(
    bool Succeeded,
    string Code,
    string Message,
    DeadLetterItemDto? Item = null);

/// <summary>Durable record for an administrative DLQ decision.</summary>
public sealed class JobDeadLetterResolution
{
    public long Id { get; set; }
    public string Queue { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public long AdminUserId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
