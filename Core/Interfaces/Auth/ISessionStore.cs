using Core.Models.Token;

namespace Core.Interfaces.Auth;

/// <summary>
/// 管理用户会话记录（<see cref="SessionRecord"/>）在持久化存储（如 Redis）中的查询与撤销。
/// <para>
/// 会话记录由 <see cref="IRefreshTokenStore.StoreRefreshTokenAsync"/> 和
/// <see cref="IRefreshTokenStore.RotateRefreshTokenAsync"/> 自动维护，
/// 调用方无需手动写入；此接口仅暴露读取和撤销操作。
/// </para>
/// <para>
/// 用户级设备索引键：<c>UDI:{userId}</c>（Redis SET），避免 KEYS/SCAN。
/// </para>
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// 查询指定用户在指定设备上的会话记录；不存在则返回 <see langword="null"/>。
    /// </summary>
    Task<SessionRecord?> GetSessionAsync(string userId, string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出用户全部活跃会话（基于用户设备索引，不使用 SCAN）。
    /// </summary>
    Task<IReadOnlyList<SessionRecord>> ListSessionsAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 撤销（删除）指定用户在指定设备上的会话记录，用于远程踢出设备。
    /// </summary>
    Task RevokeSessionAsync(string userId, string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 撤销用户全部会话（含访问令牌与刷新令牌）。
    /// </summary>
    /// <param name="exceptDeviceId">可选：保留该设备会话（例如「退出其他设备」）。</param>
    Task<int> RevokeAllSessionsAsync(string userId, string? exceptDeviceId = null, CancellationToken cancellationToken = default);
}
