namespace Core.Models.Export;

public static class AttachmentConfirmSagaStatus
{
    public const string Requested = "Requested";
    public const string StorageConfirmed = "StorageConfirmed";
    public const string MetadataScanning = "MetadataScanning";
    public const string ScanQueued = "ScanQueued";
    public const string Completed = "Completed";
    public const string Compensating = "Compensating";
    public const string Failed = "Failed";
}

/// <summary>
/// Durable intent for confirming an uploaded attachment. The HTTP request only
/// creates this row; every external side effect is retried by the saga worker.
/// </summary>
public sealed class AttachmentConfirmSaga
{
    public long Id { get; set; }
    public string AttachmentId { get; set; } = string.Empty;
    public long UserId { get; set; }
    public string ObjectKey { get; set; } = string.Empty;

    /// <summary>Upload ticket encrypted at rest; never log or persist plaintext.</summary>
    public string? ProtectedTicket { get; set; }

    public long UploaderDeletionEpoch { get; set; }
    public string? ConfirmedObjectKey { get; set; }
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    public string? OriginalName { get; set; }
    public long? ScanJobId { get; set; }

    public string Status { get; set; } = AttachmentConfirmSagaStatus.Requested;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastError { get; set; }

    public string? LeaseOwner { get; set; }
    public string? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}
