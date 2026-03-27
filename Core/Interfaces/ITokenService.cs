using System.Security.Claims;
using Core.Models.Identity;

namespace Core.Interfaces;

public interface ITokenService
{
    /// <summary>
    ///     生成访问令牌 (Access Token)
    /// </summary>
    string GenerateAccessToken(ApplicationUser user, IList<string>? roles = null);

    /// <summary>
    ///     生成刷新令牌 (Refresh Token)
    /// </summary>
    /// <returns>刷新令牌</returns>
    string GenerateRefreshToken();

    /// <summary>
    ///     验证 Access Token 是否有效
    /// </summary>
    /// <param name="token">Token</param>
    /// <param name="claims">ClaimsPrincipal</param>
    /// <returns></returns>
    bool ValidateToken(string token, out ClaimsPrincipal? claims);
}