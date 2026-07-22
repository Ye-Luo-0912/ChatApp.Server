using Core.Models.Auth;
using Core.Models.Token;

namespace Core.Interfaces;

/// <summary>
/// 定义认证模块的核心能力，包括登录、登出、注册和令牌续签。
/// </summary>
public interface IAuthService
{
    Task<LoginResult> LoginAsync(
        string account,
        string password,
        string? trustedDeviceToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>完成 MFA 挑战后签发令牌。</summary>
    Task<LoginResult> VerifyMfaAsync(string mfaToken, string code, CancellationToken cancellationToken = default);

    Task LogoutAsync(long userId, string refreshToken, CancellationToken cancellationToken = default);

    Task<UserRegistrationResult> RegisterAsync(string? username, string email, string password, CancellationToken cancellationToken = default);

    Task<TokenPairResult> RefreshLoginAsync(long account, string refreshToken, CancellationToken cancellationToken = default);

    Task<bool> IsEmailRegisteredAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// 通过邮箱验证码重置密码，并撤销全部会话。
    /// </summary>
    Task<AuthOperationResult> ResetPasswordAsync(string email, string code, string newPassword, CancellationToken cancellationToken = default);
}
