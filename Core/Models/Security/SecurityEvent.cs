namespace Core.Models.Security;

public enum SecurityEventType : short
{
    LoginSuccess = 1,
    LoginNewDevice = 2,
    LoginUnusualLocation = 3,
    PasswordChanged = 4,
    EmailChanged = 5,
    SessionRevoked = 6,
    ForceLogout = 7,
    AccountDisabled = 8,
    AccountEnabled = 9,
    RoleAssigned = 10,
    RoleRemoved = 11,
    AdminAction = 12,
    NotMeReported = 13,
    MfaEnabled = 14,
    MfaDisabled = 15,
    AccountDeletionScheduled = 16,
    ReportSubmitted = 17,
    MfaRecoveryCodesRegenerated = 18,
    UnusualLoginAcknowledged = 19,
    TrustedDeviceAdded = 20,
    TrustedDeviceRemoved = 21,
}

/// <summary>持久化安全事件（不依赖会过期的 Redis 会话）。</summary>
public sealed class SecurityEvent
{
    public long Id { get; set; }
    public long? UserId { get; set; }
    public SecurityEventType EventType { get; set; }
    public string? DeviceId { get; set; }
    public string? ClientIp { get; set; }
    public string? Location { get; set; }
    public string? Detail { get; set; }
    public string? ActorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
