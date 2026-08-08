namespace Core.Models.Export;

/// <summary>
/// 用户注销队列的派生作业视图。
/// <para>
/// 注销没有独立队列表，租约直接存放在 AspNetUsers。LeaseOwner 标识
/// Worker，LeaseToken 是每个用户本次领取独立的 fencing token。
/// </para>
/// </summary>
public sealed class AccountDeletionJob
{
    public long UserId { get; init; }
    public DateTimeOffset ScheduledAt { get; init; }
    public string LeaseOwner { get; init; } = string.Empty;
    public string LeaseToken { get; init; } = string.Empty;
    public DateTimeOffset LeaseExpiresAt { get; init; }
    public int AttemptCount { get; init; }

    /// <summary>物理删除已在用户事务中提交；后续 Complete 只需确认终态。</summary>
    public bool Terminal { get; set; }
}
