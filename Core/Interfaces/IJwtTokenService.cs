using Core.Models.DTOs.Auth;
using Core.Models.DTOs.Login;
using Core.Models.Identity;

namespace Core.Interfaces;

/// <summary>
/// 定义 JWT 访问令牌与刷新令牌相关操作。
/// </summary>
public interface IJwtTokenService : ITokenService
{
    /// <summary>
    /// 存储刷新令牌。
    /// </summary>
    /// <param name="userId">用户 ID。</param>
    /// <param name="refreshToken">刷新令牌。</param>
    Task StoreRefreshTokenAsync(string userId, string refreshToken);

    /// <summary>
    /// 验证刷新令牌是否有效。
    /// </summary>
    /// <param name="userId">关联的用户 ID。</param>
    /// <param name="refreshToken">刷新令牌。</param>
    /// <returns>刷新令牌是否有效。</returns>
    Task<bool> ValidateRefreshTokenAsync(string userId, string refreshToken);

    /// <summary>
    /// 撤销指定用户的刷新令牌，防止重复使用。
    /// </summary>
    /// <param name="userId">用户 ID。</param>
    /// <param name="refreshToken">需要撤销的刷新令牌。</param>
    Task RevokeRefreshTokenAsync(string userId, string refreshToken);

    /// <summary>
    /// 获取关联用户的刷新令牌信息。
    /// </summary>
    /// <param name="userId">用户 ID。</param>
    /// <param name="refreshToken">刷新令牌字符串。</param>
    /// <returns>刷新令牌记录。</returns>
    Task<RefreshToken?> GetRefreshTokenAsync(string userId, string refreshToken);

    /// <summary>
    /// 根据用户和角色签发登录所需的访问令牌和刷新令牌。
    /// </summary>
    Task<TokenIssueResult> IssueLoginTokensAsync(ApplicationUser user, IList<string> roles);

    /// <summary>
    /// 验证旧刷新令牌是否合法，若合法则立即销毁，避免重复使用。
    /// </summary>
    Task<bool> ValidateAndRevokeRefreshTokenAsync(string userId, string refreshToken);

    /// <summary>
    /// 轮换刷新令牌，撤销旧令牌并存储新令牌。
    /// </summary>
    Task RotateRefreshTokenAsync(string userId, string oldRefreshToken, string newRefreshToken);
}