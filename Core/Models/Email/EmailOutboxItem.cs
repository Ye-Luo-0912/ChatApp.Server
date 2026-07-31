namespace Core.Models.Email;

public class EmailOutboxItem
{
    public long Id { get; set; }
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsHtml { get; set; }
    public EmailOutboxStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }

    /// <summary>业务类型，如 verification / notification。</summary>
    public string? EmailType { get; set; }

    /// <summary>幂等键；相同键在 Pending/Processing/Failed 期间不可重复入队。</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>领取处理租约时间。</summary>
    public DateTime? LockedAt { get; set; }

    /// <summary>领取者标识（实例 Id）。</summary>
    public string? LockOwner { get; set; }

    /// <summary>
    /// 租约 fencing token：每次领取时生成的随机 GUID，用于替代 LockedAt 作为完成/失败/释放操作的匹配条件。
    /// <para>LockedAt 是 timestamptz（微秒精度），而 .NET DateTime 是 tick 精度，
    /// 写入后 in-memory 值与 DB 值可能不一致导致 WHERE 匹配失败。LeaseToken 是字符串，无精度问题。</para>
    /// </summary>
    public string? LeaseToken { get; set; }
}
