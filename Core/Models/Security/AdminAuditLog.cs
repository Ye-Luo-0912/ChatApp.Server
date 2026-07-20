namespace Core.Models.Security;

/// <summary>管理员操作审计。</summary>
public sealed class AdminAuditLog
{
    public long Id { get; set; }
    public long AdminUserId { get; set; }
    public long? TargetUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? Detail { get; set; }
    public string? ClientIp { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
