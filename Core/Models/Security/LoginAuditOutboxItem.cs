namespace Core.Models.Security;

/// <summary>
/// Durable login-audit delivery queue. Login must not fail because a
/// secondary audit write is temporarily unavailable, but once the login
/// transaction commits this row gives the audit event a retryable boundary.
/// </summary>
public enum LoginAuditOutboxStatus : byte
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    DeadLetter = 4,
}

public sealed class LoginAuditOutboxItem
{
    public long Id { get; set; }
    public long? UserId { get; set; }
    public SecurityEventType EventType { get; set; }
    public string? DeviceId { get; set; }
    public string? SessionId { get; set; }
    public string? ClientIp { get; set; }
    public string? Location { get; set; }
    public string? Detail { get; set; }
    public string? ActorUserId { get; set; }
    public LoginAuditOutboxStatus Status { get; set; } = LoginAuditOutboxStatus.Pending;
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
