using Core.Models.Token;

namespace Core.Interfaces.Auth;

/// <summary>
/// 管理访问令牌（Access Token）在持久化存储（如 Redis）中的完整生命周期。
/// <para>
/// 键名在存储前对 token 做 SHA-256 哈希，避免原始令牌出现在缓存键或日志中。
/// </para>
/// </summary>
public interface IAccessTokenStore
{
    /// <summary>
    /// 写入访问令牌及其元数据，并设置绝对过期时间。
    /// </summary>
    Task StoreAccessTokenAsync(string token, AccessTokenData data, TimeSpan expiry, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询访问令牌对应的元数据。令牌不存在或已被撤销则返回 <see langword="null"/>。
    /// </summary>
    Task<AccessTokenData?> GetAccessTokenAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// 立即撤销访问令牌，使其在到期前失效（适用于主动登出场景）。
    /// </summary>
    Task RevokeAccessTokenAsync(string token, CancellationToken cancellationToken = default);
}
