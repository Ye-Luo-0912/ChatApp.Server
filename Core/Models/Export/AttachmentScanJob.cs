namespace Core.Models.Export;

public static class AttachmentScanJobStatus
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Finalizing = "Finalizing";
    public const string Done = "Done";
    public const string DeadLetter = "DeadLetter";
}

/// <summary>
/// 附件内容扫描作业：Confirm 入队，Worker 原子领取后扫描；
/// 仅在 Realtime 元数据进入 Confirmed/Rejected 后才 Done。
/// </summary>
public sealed class AttachmentScanJob
{
    public long Id { get; set; }
    public string AttachmentId { get; set; } = string.Empty;
    public string ObjectKey { get; set; } = string.Empty;
    public long UserId { get; set; }
    public string? ContentType { get; set; }
    public string? OriginalName { get; set; }
    public long SizeBytes { get; set; }
    public string Status { get; set; } = AttachmentScanJobStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>领取该作业的实例标识。</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>租约过期后可被其他实例重新领取（崩溃恢复）。</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>
    /// 租约 fencing token：每次领取时生成的随机 GUID，用于替代 LeaseExpiresAt 精度匹配。
    /// <para>P0-5.2：完成/失败/续租操作必须匹配 Id + Status + LeaseOwner + LeaseToken，
    /// 防止租约过期后被另一实例重新领取后，旧持有者仍覆盖终态造成重复扫描/重复 Confirm。</para>
    /// </summary>
    public string? LeaseToken { get; set; }
}
