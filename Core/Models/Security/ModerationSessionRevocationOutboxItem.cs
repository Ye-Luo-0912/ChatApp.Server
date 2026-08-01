namespace Core.Models.Security;

/// <summary>
/// 由审核封禁事务写入的会话撤销命令。
/// Redis 会话状态不是耐久边界；该记录确保已提交的封禁最终会触发可重试的撤销。
/// </summary>
public enum ModerationSessionRevocationOutboxStatus : byte
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    Dead = 4,
    Skipped = 5,
}

/// <summary>
/// 审核封禁对应的耐久会话撤销 Outbox。
/// <see cref="ExpectedSecurityVersion"/> 与 <see cref="ExpectedBanUntil"/> 是防止旧封禁命令撤销新会话的业务栅栏；
/// <see cref="LeaseToken"/> 是领取、完成和失败状态变更的 fencing token。
/// </summary>
public sealed class ModerationSessionRevocationOutboxItem
{
    public long Id { get; set; }
    public long SourceReportId { get; set; }
    public long UserId { get; set; }
    public long ExpectedSecurityVersion { get; set; }
    public DateTimeOffset ExpectedBanUntil { get; set; }
    public ModerationSessionRevocationOutboxStatus Status { get; set; } = ModerationSessionRevocationOutboxStatus.Pending;
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
