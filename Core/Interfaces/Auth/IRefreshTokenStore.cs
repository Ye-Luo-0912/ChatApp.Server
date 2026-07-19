using Core.Models.Token;

namespace Core.Interfaces.Auth;

/// <summary>
/// 管理刷新令牌（Refresh Token）在持久化存储（如 Redis）中的完整生命周期。
/// <para>
/// 刷新令牌绑定设备信息（DeviceId、IP、UserAgent），跨设备使用视为非法。
/// </para>
/// </summary>
public interface IRefreshTokenStore
{
    /// <summary>
    /// 写入刷新令牌，并将当前请求的设备信息一并绑定到记录中。
    /// </summary>
    Task StoreRefreshTokenAsync(string userId, string refreshToken);

    /// <summary>
    /// 校验刷新令牌是否合法：存在于存储中、未过期、且设备 ID 与当前请求一致。
    /// </summary>
    Task<bool> ValidateRefreshTokenAsync(string userId, string refreshToken);

    /// <summary>
    /// 撤销（删除）指定的刷新令牌，用于主动登出。
    /// </summary>
    Task RevokeRefreshTokenAsync(string userId, string refreshToken);

    /// <summary>
    /// 查询刷新令牌元数据，不存在则返回 <see langword="null"/>。
    /// </summary>
    Task<RefreshToken?> GetRefreshTokenAsync(string userId, string refreshToken);

    /// <summary>
    /// 先校验令牌有效性（含设备匹配），通过后立即撤销——一次性消费语义。
    /// 适用于令牌轮换前的合法性检验。
    /// </summary>
    Task<bool> ValidateAndRevokeRefreshTokenAsync(string userId, string refreshToken);

    /// <summary>
    /// 原子地撤销旧令牌并写入新令牌，保证任意时刻每个设备只存在一张有效刷新令牌。
    /// </summary>
    /// <returns>CAS 成功为 <see langword="true"/>；旧令牌无效或已被并发消费为 <see langword="false"/>。</returns>
    Task<bool> RotateRefreshTokenAsync(string userId, string oldRefreshToken, string newRefreshToken);
}
