namespace ChatApp.Server.Models.Calls;

/// <summary>
/// call grant 签发配置（GROUP-CALL-MIDJOIN-1）。
/// <para>
/// 双人 grant 保持 60s 短生命周期不变；群组 grant 的有效期可配（缺省 4 小时）——
/// 无状态中继按 grant 过期时间放行成员信令，短 TTL 会使群组会话在 60s 后无法
/// End/重连/中途加人（call_grant_expired）。配置节：<c>CallGrant</c>。
/// </para>
/// </summary>
public sealed class CallGrantOptions
{
    public const string SectionName = "CallGrant";

    /// <summary>群组 grant 有效期缺省值（秒）：4 小时。</summary>
    public const int DefaultGroupGrantLifetimeSeconds = 4 * 60 * 60;

    /// <summary>群组 grant 有效期（秒）。双人 grant 恒为 60s，不经本配置。</summary>
    public int GroupGrantLifetimeSeconds { get; set; } = DefaultGroupGrantLifetimeSeconds;
}
