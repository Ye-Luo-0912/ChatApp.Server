namespace Core.Models.Security;

public enum SecuritySessionRevocationOutboxStatus : byte
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    DeadLetter = 4,
    Skipped = 5,
}

/// <summary>
/// Durable post-commit revocation command emitted by the security mutation
/// coordinator. SecurityVersion remains the correctness fence; this row makes
/// Redis session and trusted-device cleanup retryable.
/// </summary>
public sealed class SecuritySessionRevocationOutboxItem
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long ExpectedSecurityVersion { get; set; }
    public string? ExceptDeviceId { get; set; }
    public bool RevokeTrustedDevices { get; set; }
    public SecurityEventType EventType { get; set; }
    public SecuritySessionRevocationOutboxStatus Status { get; set; } = SecuritySessionRevocationOutboxStatus.Pending;
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LeaseOwner { get; set; }
    public string? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}
