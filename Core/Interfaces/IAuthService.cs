using Core.Models.Auth;
using Core.Models.Token;

namespace Core.Interfaces;

/// <summary>
/// 定义认证模块的核心能力，包括登录、登出、注册和令牌续签。
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// 校验用户名和密码，并返回登录结果。
    /// </summary>
    Task<LoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// 撤销当前用户的刷新令牌，实现登出。
    /// </summary>
    Task LogoutAsync(long userId, string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// 注册新用户。
    /// </summary>
    Task<UserRegistrationResult> RegisterAsync(string? username, string email, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// 使用刷新令牌换取新的令牌对。
    /// </summary>
    Task<TokenPairResult> RefreshLoginAsync(long account, string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// 检查给定的电子邮件地址是否已被注册。
    /// </summary>
    /// <param name="email">要检查的电子邮件地址。</param>
    /// <returns>如果电子邮件已注册，则返回true；否则返回false。</returns>
    Task<bool> IsEmailRegisteredAsync(string email, CancellationToken cancellationToken = default);
}
