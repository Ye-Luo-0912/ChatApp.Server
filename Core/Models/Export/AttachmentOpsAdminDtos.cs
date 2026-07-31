namespace Core.Models.Export;

public sealed class AttachmentOpsActionRequest
{
    public string? Reason { get; set; }
}

/// <summary>附件运维只读汇总（Admin / curl）。</summary>
public sealed record AttachmentOpsOrphansDto(
    bool MetadataAvailable,
    string? UnavailableReason,
    int OrphanAgeMinutes,
    int StuckScanningMinutes,
    long ConfirmedUnboundPastAgeCount,
    long AbandonedUploadingPastAgeCount,
    long StuckScanningCount,
    long OldestConfirmedUnboundAgeMs,
    long OldestUploadingAgeMs,
    long OldestStuckScanningAgeMs,
    IReadOnlyList<AttachmentOpsSampleRowDto> WorstConfirmedUnbound,
    IReadOnlyList<AttachmentOpsSampleRowDto> WorstUploading,
    IReadOnlyList<AttachmentOpsSampleRowDto> WorstStuckScanning,
    long GeneratedAtMs);

public sealed record AttachmentOpsSampleRowDto(
    string AttachmentId,
    string ObjectKey,
    long UploaderUserId,
    string StatusName,
    short Status,
    long SizeBytes,
    long CreatedAtMs,
    long? AgeMs,
    string? LastError = null);

public sealed record AttachmentOpsDeleteFailuresDto(
    long PendingCount,
    long DoneCount,
    long HighAttemptPendingCount,
    int HighAttemptThreshold,
    int MaxAttemptCount,
    long? OldestPendingAtMs,
    long? OldestPendingAgeMs,
    IReadOnlyList<AttachmentOpsDeleteJobRowDto> WorstPending,
    long GeneratedAtMs);

public sealed record AttachmentOpsDeleteJobRowDto(
    long Id,
    string ObjectKey,
    string? AttachmentId,
    long? UserId,
    string Status,
    int AttemptCount,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset CreatedAt,
    string? LastError);

public sealed record AttachmentOpsScanBacklogDto(
    long PendingCount,
    long ProcessingCount,
    long FinalizingCount,
    long DeadLetterCount,
    long DoneCount,
    long RetryingCount,
    long ExhaustedLikeCount,
    int MaxScanAttempts,
    long? OldestPendingAtMs,
    long? OldestPendingAgeMs,
    long? OldestProcessingAtMs,
    long? OldestProcessingAgeMs,
    IReadOnlyList<AttachmentOpsScanJobRowDto> WorstOpen,
    long GeneratedAtMs);

public sealed record AttachmentOpsScanJobRowDto(
    long Id,
    string AttachmentId,
    string ObjectKey,
    long UserId,
    string Status,
    int AttemptCount,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LeaseExpiresAt,
    string? LastError);

public sealed record AttachmentScanAuditDto(
    long Id,
    long ScanJobId,
    string AttachmentId,
    string ObjectKey,
    long UserId,
    int AttemptCount,
    string? ContentType,
    long SizeBytes,
    string EngineName,
    string EngineVersion,
    string Verdict,
    bool Allowed,
    bool IsTransient,
    string? Reason,
    DateTimeOffset CreatedAt);

/// <summary>廉价提示：元数据 size 汇总 + 配置；不做 Redis KEYS / 全盘扫描。</summary>
public sealed record AttachmentOpsHintsDto(
    bool MetadataAvailable,
    string? UnavailableReason,
    string StorageProvider,
    long? ActiveAttachmentCount,
    long? ActiveSizeBytesSum,
    int DownloadTicketMinutes,
    string DownloadTicketNote,
    IReadOnlyList<string> RelatedMetricNames,
    long GeneratedAtMs);
