using Core.Models.Token;

namespace Core.Interfaces.Auth;

/// <summary>
/// 管理用户会话记录（<see cref="SessionRecord"/>）在持久化存储（如 Redis）中的查询与撤销。
/// <para>
/// 会话记录由 <see cref="IRefreshTokenStore.StoreRefreshTokenAsync"/> 和
/// <see cref="IRefreshTokenStore.RotateRefreshTokenAsync"/> 自动维护，
/// 调用方无需手动写入；此接口仅暴露读取和撤销操作。
/// </para>
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// 查询指定用户在指定设备上的会话记录；不存在则返回 <see langword="null"/>。
    /// </summary>
    Task<SessionRecord?> GetSessionAsync(string userId, string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 撤销（删除）指定用户在指定设备上的会话记录，用于远程踢出设备。
    /// </summary>
    Task RevokeSessionAsync(string userId, string deviceId, CancellationToken cancellationToken = default);
}
