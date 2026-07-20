namespace Core.Models.Notifications;

public enum NotificationOutboxStatus : byte
{
    Pending = 0,
    Processing = 1,
    Sent = 2,
    Failed = 3,
    Dead = 4,
}

public sealed class NotificationOutboxItem
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool PreferEmail { get; set; }
    public NotificationOutboxStatus Status { get; set; } = NotificationOutboxStatus.Pending;
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LockedAt { get; set; }
    public string? LockOwner { get; set; }

    /// <summary>站内通知已投递时间；非空则重试跳过站内写入。</summary>
    public DateTimeOffset? InAppDeliveredAt { get; set; }

    /// <summary>邮件已投递时间；非空则重试跳过邮件发送。</summary>
    public DateTimeOffset? EmailDeliveredAt { get; set; }
}
