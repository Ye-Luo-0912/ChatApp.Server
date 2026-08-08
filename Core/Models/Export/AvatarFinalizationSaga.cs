namespace Core.Models.Export;

public static class AvatarFinalizationSagaStatus
{
    public const string Requested = "Requested";
    public const string StorageConfirmed = "StorageConfirmed";
    public const string MetadataCommitted = "MetadataCommitted";
    public const string Completed = "Completed";
    public const string Compensating = "Compensating";
    public const string Abandoned = "Abandoned";
    public const string Failed = "Failed";
}

/// <summary>
/// Durable avatar finalization intent. Object confirmation, user metadata CAS,
/// publication tagging and old-object deletion are independent retryable
/// stages; no request lifetime is used as the recovery boundary.
/// </summary>
public sealed class AvatarFinalizationSaga
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string ObjectKey { get; set; } = string.Empty;
    public string? ProtectedTicket { get; set; }
    public string? OldAvatarUrl { get; set; }
    public long ExpectedAvatarVersion { get; set; }
    public long UploaderDeletionEpoch { get; set; }
    public string? FinalObjectKey { get; set; }
    public string? PublicUrl { get; set; }
    public string Status { get; set; } = AvatarFinalizationSagaStatus.Requested;
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
