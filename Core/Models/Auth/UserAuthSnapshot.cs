namespace Core.Models.Auth;

using Core.Models.Identity;
using System.Text.Json.Serialization;

/// <summary>
/// 登录/刷新使用的短期认证快照。SecurityVersion 是缓存的版本栅栏，
/// 角色或账户安全状态变化后，旧快照不会被重新使用。
/// </summary>
public sealed class UserAuthSnapshot
{
    public long UserId { get; set; }
    public string? UserName { get; set; }
    public long SecurityVersion { get; set; }
    public AccountState AccountState { get; set; } = AccountState.Active;
    public string[] Roles { get; set; } = [];

    /// <summary>
    /// 表示 Roles 已加载。认证 handler 只需要下面的 fence 字段，缓存 miss
    /// 时不应为了构造 Claims 再做一次角色查询；登录/角色读取才会补齐它。
    /// </summary>
    public bool RolesLoaded { get; set; }

    /// <summary>
    /// 用户名和角色均已从权威数据源加载。普通认证只有拿到完整快照后
    /// 才能构造 Claims，避免回退到每个 AT 自带的重复角色数据。
    /// </summary>
    [JsonIgnore]
    public bool ClaimsLoaded => RolesLoaded && UserName is not null;

    /// <summary>账户锁定开关与截止时间组成的认证 fence。</summary>
    public bool LockoutEnabled { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }

    /// <summary>
    /// Compatibility name for callers that still use the Identity field
    /// name. The serialized fence has one canonical LockoutUntil value.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset? LockoutEnd
    {
        get => LockoutUntil;
        set => LockoutUntil = value;
    }

    /// <summary>
    /// Roles share the same durable security fence today. Keeping the named
    /// field in the snapshot makes that contract explicit for cache readers.
    /// </summary>
    public long RoleVersion
    {
        get => SecurityVersion;
        set => SecurityVersion = value;
    }

    /// <summary>Distributed snapshot expiry; zero means legacy/unbounded data.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>审核封禁截止时间；未来时间表示当前禁止认证。</summary>
    public DateTimeOffset? BanUntil { get; set; }

    /// <summary>非空表示账户已进入注销生命周期，普通会话不得继续使用。</summary>
    public DateTimeOffset? DeletionScheduledAt { get; set; }

    public AccountState EffectiveAccountState(DateTimeOffset now) =>
        AccountState == AccountState.Deleted
            ? AccountState.Deleted
            : DeletionScheduledAt is { } scheduledAt && scheduledAt > now
                ? AccountState.DeletionPending
                : AccountState;

    public bool IsDeletionPendingAt(DateTimeOffset now) =>
        EffectiveAccountState(now) == AccountState.DeletionPending;

    public bool IsAllowedAt(DateTimeOffset now) =>
        !IsExpiredAt(now)
        && !(LockoutEnabled && LockoutUntil is { } lockoutEnd && lockoutEnd > now)
        && !(BanUntil is { } banUntil && banUntil > now)
        && EffectiveAccountState(now) != AccountState.Deleted
        && (AccountState != AccountState.DeletionPending
            || DeletionScheduledAt is { } pendingAt && pendingAt > now)
        && !(DeletionScheduledAt is { } scheduledAt && scheduledAt <= now);

    public bool IsExpiredAt(DateTimeOffset now) =>
        ExpiresAt != default && ExpiresAt <= now;
}
