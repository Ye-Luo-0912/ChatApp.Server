namespace Core.Models.Security;

/// <summary>
/// Durable queue item for post-login geo/ASN analysis. The login request only
/// appends this small row; network calls, history reads and notifications run
/// in the Worker role.
/// </summary>
public enum LoginRiskOutboxStatus : byte
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    DeadLetter = 4,
}

public sealed class LoginRiskOutboxItem
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string? ClientIp { get; set; }
    public string? DeviceId { get; set; }
    public bool IsNewDevice { get; set; }
    public bool IpChanged { get; set; }
    public string? SessionId { get; set; }
    /// <summary>Decision-rule version captured when the login signal was written.</summary>
    public int RuleVersion { get; set; } = 1;
    public LoginRiskOutboxStatus Status { get; set; } = LoginRiskOutboxStatus.Pending;
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
