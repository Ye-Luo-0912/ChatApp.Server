namespace Core.Models.Export;

public static class AttachmentBlobDeleteJobStatus
{
    public const string Pending = "Pending";
    /// <summary>
    /// Avatar final object exists, but the owning user row has not published
    /// it yet. The row becomes deletable after NextAttemptAt unless the
    /// publication reconciliation marks it Published.
    /// </summary>
    public const string AwaitingPublication = "AwaitingPublication";
    /// <summary>
    /// The object is currently referenced by the user row. This is not a
    /// deletion terminal state: a later avatar replacement may enqueue a new
    /// delete tombstone for the same object key.
    /// </summary>
    public const string Published = "Published";
    public const string Processing = "Processing";
    public const string Done = "Done";
    public const string DeadLetter = "DeadLetter";
}

public static class AttachmentBlobDeleteStorageKind
{
    public const string Attachment = "attachment";
    public const string Avatar = "avatar";
}

/// <summary>
/// 附件 blob 删除墓碑：账号删除 / MarkAbandoned / AttachmentBlobsPurge 入队，
/// Worker 带退避重试，失败写入 LastError（不静默吞掉）。
/// </summary>
public sealed class AttachmentBlobDeleteJob
{
    public long Id { get; set; }
    public string ObjectKey { get; set; } = string.Empty;
    public string StorageKind { get; set; } = AttachmentBlobDeleteStorageKind.Attachment;
    public string? AttachmentId { get; set; }
    public long? UserId { get; set; }
    public string Status { get; set; } = AttachmentBlobDeleteJobStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>当前领取者；与 <see cref="LeaseToken"/> 一起构成完成/失败更新的 fencing 条件。</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>租约到期后作业可由其他实例重新领取。</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>
    /// 每次领取生成的随机 token。旧 Worker 只能在 token 仍匹配时将外部删除结果写回，
    /// 因而不会覆盖已易主的作业。
    /// </summary>
    public string? LeaseToken { get; set; }
}
