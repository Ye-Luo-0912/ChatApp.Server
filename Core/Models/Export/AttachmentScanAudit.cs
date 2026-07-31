namespace Core.Models.Export;

/// <summary>
/// 每次附件扫描尝试的不可变审计记录。扫描作业本身会重试/清理，
/// 这里保留引擎、版本、判定和原因，供安全审计与人工复核追溯。
/// </summary>
public sealed class AttachmentScanAudit
{
    public long Id { get; set; }
    public long ScanJobId { get; set; }
    public string AttachmentId { get; set; } = string.Empty;
    public string ObjectKey { get; set; } = string.Empty;
    public long UserId { get; set; }
    public int AttemptCount { get; set; }
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    public string EngineName { get; set; } = "unknown";
    public string EngineVersion { get; set; } = "unknown";
    public string Verdict { get; set; } = "transient";
    public bool Allowed { get; set; }
    public bool IsTransient { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
