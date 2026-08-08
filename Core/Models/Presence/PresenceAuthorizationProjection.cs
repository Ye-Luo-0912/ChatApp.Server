namespace Core.Models.Presence;

/// <summary>
/// Presence 授权的短期派生投影。
/// Block deny marker 与普通授权结果共用载荷，以便一次批量读取；
/// epoch 记录投影所依据的 Server 关系和 Realtime 成员状态版本。
/// </summary>
public sealed class PresenceAuthorizationProjection
{
    public bool IsBlockDenyMarker { get; set; }
    public bool Allowed { get; set; }
    public long RelationshipEpoch { get; set; }
    /// <summary>投影依赖成员关系时，记录 watcher 的用户成员 epoch。</summary>
    public long WatcherMembershipEpoch { get; set; }
    /// <summary>投影依赖成员关系时，记录 target 的用户成员 epoch。</summary>
    public long TargetMembershipEpoch { get; set; }
    /// <summary>
    /// 非好友授权依赖共享会话成员关系；该关系变化时必须比较两个用户 epoch。
    /// 好友授权不依赖成员关系，避免无关群成员变更驱逐好友命中。
    /// </summary>
    public bool MembershipDependent { get; set; }

    /// <summary>
    /// 兼容旧投影字段。新投影使用 watcher/target 两个用户 epoch，
    /// 不再把 Realtime 行时间戳当作当前版本来源。
    /// </summary>
    [Obsolete("Use WatcherMembershipEpoch and TargetMembershipEpoch.")]
    public long MembershipEpoch { get; set; }
}
