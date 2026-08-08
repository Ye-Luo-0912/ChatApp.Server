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
    /// <summary>消息证据元数据快照，必须始终是完整合法 JSON。</summary>
    public string? EvidenceSnapshot { get; set; }
    /// <summary>证据正文的有界预览；不把可变正文拼接进截断 JSON。</summary>
    public string? EvidenceBodyPreview { get; set; }
    /// <summary>获取证据时服务端返回的正文哈希。</summary>
    public string? EvidenceContentHash { get; set; }
    /// <summary>举报去重键：举报人、目标、UTC 日桶。</summary>
    public string? DedupeKey { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public UserReportStatus Status { get; set; } = UserReportStatus.Open;
    public string? AppealNote { get; set; }
    public DateTimeOffset? BanUntil { get; set; }
    public long? ReviewedByAdminId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
