namespace Core.Models.Moderation;

public enum UserReportStatus : byte
{
    Open = 0,
    Reviewed = 1,
    ActionTaken = 2,
    Rejected = 3,
    Appealed = 4,
}

public enum UserReportTargetType : byte
{
    User = 1,
    Message = 2,
}

public sealed class UserReport
{
    public long Id { get; set; }
    public long ReporterId { get; set; }
    public UserReportTargetType TargetType { get; set; }
    public long? TargetUserId { get; set; }
    public string? TargetMessageId { get; set; }
    /// <summary>消息证据快照（仅服务端消息原文/发送人/时间/内容哈希；禁止使用举报人 detail）。</summary>
    public string? EvidenceSnapshot { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public UserReportStatus Status { get; set; } = UserReportStatus.Open;
    public string? AppealNote { get; set; }
    public DateTimeOffset? BanUntil { get; set; }
    public long? ReviewedByAdminId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
