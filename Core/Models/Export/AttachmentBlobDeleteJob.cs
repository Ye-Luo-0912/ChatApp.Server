namespace Core.Models.Export;

public static class AttachmentBlobDeleteJobStatus
{
    public const string Pending = "Pending";
    public const string Done = "Done";
}

/// <summary>
/// 附件 blob 删除墓碑：账号删除 / MarkAbandoned / AttachmentBlobsPurge 入队，
/// Worker 带退避重试，失败写入 LastError（不静默吞掉）。
/// </summary>
public sealed class AttachmentBlobDeleteJob
{
    public long Id { get; set; }
    public string ObjectKey { get; set; } = string.Empty;
    public string? AttachmentId { get; set; }
    public long? UserId { get; set; }
    public string Status { get; set; } = AttachmentBlobDeleteJobStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastError { get; set; }
}
