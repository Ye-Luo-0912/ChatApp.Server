namespace Core.Models.Export;

/// <summary>
/// Durable projection of a fenced attachment scan verdict into Realtime metadata.
/// A scan worker may only create this record while it owns the scan lease; a
/// separate projector performs the external side effects with retry and DLQ state.
/// </summary>
public sealed class AttachmentScanProjection
{
    public long Id { get; set; }
    public long ScanJobId { get; set; }
    public string AttachmentId { get; set; } = string.Empty;
    public string ObjectKey { get; set; } = string.Empty;
    public long UserId { get; set; }
    public long UploaderDeletionEpoch { get; set; }
    /// <summary>Monotonic scan generation used by the target-side CAS fence.</summary>
    public long ScanVersion { get; set; }
    public string? ContentType { get; set; }
    public string? OriginalName { get; set; }
    public long SizeBytes { get; set; }
    public string? ContentHash { get; set; }
    /// <summary>
    /// Object-store ETag observed on the exact stream that was scanned. S3 promotion
    /// must use it as an If-Match fence so a later client PUT cannot be confirmed.
    /// </summary>
    public string? SourceEntityTag { get; set; }
    public string Outcome { get; set; } = AttachmentScanProjectionOutcome.Confirmed;
    public string? RejectionReason { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = AttachmentScanProjectionStatus.Pending;
    public string? LastError { get; set; }
    public string? LeaseOwner { get; set; }
    public string? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}

public static class AttachmentScanProjectionStatus
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Done = "Done";
    public const string DeadLetter = "DeadLetter";
}

public static class AttachmentScanProjectionOutcome
{
    public const string Confirmed = "Confirmed";
    public const string Rejected = "Rejected";
    public const string Abandoned = "Abandoned";
}
