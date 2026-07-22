namespace Core.Models.Export;

public static class DataExportJobStatus
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Ready = "Ready";
    public const string Failed = "Failed";
    public const string Consumed = "Consumed";
    public const string Expired = "Expired";
    /// <summary>Blob 删除失败后的墓碑：保留 ObjectKey，由 Worker 后台重试删除。</summary>
    public const string PendingDelete = "PendingDelete";
}

/// <summary>账号数据导出作业（跨实例持久化）。</summary>
public sealed class DataExportJob
{
    public string Id { get; set; } = string.Empty;
    public long UserId { get; set; }
    public string Status { get; set; } = DataExportJobStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public string? ObjectKey { get; set; }
    public string? Error { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int AttemptCount { get; set; }
}
