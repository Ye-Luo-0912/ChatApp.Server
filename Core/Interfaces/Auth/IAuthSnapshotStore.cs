using Core.Models.Auth;

namespace Core.Interfaces.Auth;

/// <summary>
/// 用户认证快照的专用读取边界。
/// <para>
/// 普通认证请求只读取本机 L1；L1 未命中才读取 Garnet，缓存不可用时由实现
/// 回退到权威数据库。该接口不提供隐藏回源工厂或分布式锁。
/// </para>
/// </summary>
public interface IAuthSnapshotStore : IUserAuthorizationFence
{
    Task<UserAuthSnapshot?> GetAsync(
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>安全变更提交后刷新派生快照；缓存故障不得回滚权威变更。</summary>
    Task SetAsync(
        UserAuthSnapshot snapshot,
        CancellationToken cancellationToken = default);

    /// <summary>安全变更后的本机/分布式快照失效。</summary>
    Task InvalidateAsync(
        long userId,
        long? minimumSecurityVersion = null,
        bool failOnCacheError = false,
        CancellationToken cancellationToken = default);
}
