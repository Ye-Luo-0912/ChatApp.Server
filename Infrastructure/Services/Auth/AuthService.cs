using Core.Exceptions;
using Core.Interfaces;
using Core.Models.Auth;
using Core.Models.Identity;
using Core.Models.Token;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Core.Settings;

namespace Infrastructure.Services.Auth;

/// <summary>
/// 处理登录、登出、注册和令牌续签等认证流程。
/// </summary>
public class AuthService(
    UserDbContext db,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    ITsidGenerator tsidGenerator,
    IOptions<RealtimeGatewayOptions> realtimeGatewayOptions,
    ILogger<AuthService> logger) : IAuthService
{
    private const int MaxFailedAccessAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly ILogger<AuthService> _logger = logger;
    private readonly UserDbContext _db = db;
    private readonly IPasswordHasher _passwordHasher = passwordHasher;
    private readonly ITokenService _tokenService = tokenService;
    private readonly RealtimeGatewayOptions _realtimeGateway = realtimeGatewayOptions.Value;

    /// <summary>
    /// 使用提供的账户和密码进行登录。
    /// </summary>
    /// <param name="account">用户的账户名。</param>
    /// <param name="password">用户的密码。</param>
    /// <returns>返回一个<see cref="LoginResult"/>对象，包含登录结果信息。如果登录成功，则包括访问令牌、刷新令牌及其过期时间等；如果失败，则返回错误信息及状态。</returns>
    /// <exception cref="IdentityException">当登录过程中发生异常时抛出。</exception>
    public async Task<LoginResult> LoginAsync(string account, string password)
    {
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
            return LoginResult.Fail("账号或密码不能为空", LoginCheckStatus.InvalidCredentials);

        try
        {
            // 先统一校验账户状态和密码正确性。
            var (status, user) = await VerifyUserCredentialsAsync(account, password);
            if (user is null)
                return LoginResult.Fail("用户名或密码错误 / 账户已被锁定", status);

            var roles = await GetRolesAsync(user.Id);
            var tokens = await _tokenService.IssueLoginTokensAsync(user, roles);

            // 记录上次登录时间后再更新，供登录响应携带「异地登录提醒」
            var previousLoginDate = user.LastLoginDate;
            await UpdateLastLoginAsync(user);

            var tcpServer = new ServerEndPoint
            {
                Host = _realtimeGateway.Host,
                Port = _realtimeGateway.Port,
                Name = _realtimeGateway.Name
            };

            _logger.LogInformation("用户 {Username} 登录成功", account);
            return LoginResult.Success(user, previousLoginDate, tokens.SessionId, tokens.DeviceIdHash, tokens.AccessToken, tokens.AccessTokenExpiresAtUtc, tokens.RefreshToken, tokens.RefreshTokenExpiresAtUtc, ref tcpServer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "用户 {Username} 登录时发生异常", account);
            throw new IdentityException("登录过程中发生错误", ex);
        }
    }

    /// <summary>
    /// 撤销指定用户的刷新令牌，实现登出。
    /// </summary>
    public async Task LogoutAsync(long userId, string refreshToken)
    {
        try
        {
            await _tokenService.RevokeRefreshTokenAsync(userId.ToString(), refreshToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "撤销刷新令牌失败: {UserId}", userId);
            throw new IdentityException("登出失败", ex);
        }
    }

    /// <summary>
    /// 使用提供的用户名、邮箱和密码进行用户注册。
    /// </summary>
    /// <param name="username">用户的用户名，如果未提供，则使用邮箱作为用户名。</param>
    /// <param name="email">用户的邮箱地址。</param>
    /// <param name="password">用户的密码。</param>
    /// <returns>返回一个<see cref="UserRegistrationResult"/>对象，包含注册结果信息。如果注册成功，则包括用户ID和用户名；如果失败，则返回错误信息及状态。</returns>
    /// <exception cref="IdentityException">当注册过程中发生异常时抛出。</exception>
    public async Task<UserRegistrationResult> RegisterAsync(string? username, string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return UserRegistrationResult.Fail([], "账号或者密码不能为空");

        var name       = string.IsNullOrWhiteSpace(username) ? email : username.Trim();
        var normalizedEmail = email.Trim().ToUpperInvariant();
        var normalizedName  = name.ToUpperInvariant();

        // 邮箱和用户名分开检查，以便返回精确的错误提示
        if (await _db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail))
            return UserRegistrationResult.Fail([], "该邮箱已被注册");

        if (await _db.Users.AnyAsync(u => u.NormalizedUserName == normalizedName))
            return UserRegistrationResult.Fail([], "该用户名已被使用");

        var user = new ApplicationUser
        {
            Id                 = tsidGenerator.GenerateTsid(),
            UserName           = name,
            NormalizedUserName = normalizedName,
            Email              = email.Trim(),
            NormalizedEmail    = normalizedEmail,
            // BCrypt 哈希密码，WorkFactor 在 BcryptPasswordHasher 中统一配置
            PasswordHash   = _passwordHasher.HashPassword(password),
            SecurityStamp  = Guid.NewGuid().ToString(),
            LockoutEnabled = true
        };

        try
        {
            _db.Users.Add(user);
            await _db.SaveChangesAsync();
            _logger.LogInformation("成功创建用户 {UserId}", user.Id);
            return UserRegistrationResult.Success(user.Id, user.UserName ?? name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建用户时发生异常");
            throw new IdentityException("用户创建失败", ex);
        }
    }

    /// <summary>
    /// 刷新登录令牌对。
    /// </summary>
    /// <param name="account">用户的唯一标识符。</param>
    /// <param name="refreshToken">用于刷新的旧刷新令牌。</param>
    /// <returns>包含新访问令牌和刷新令牌的结果，如果失败则返回错误类型。</returns>
    /// <exception cref="IdentityException">在刷新令牌过程中发生异常时抛出。</exception>
    public async Task<TokenPairResult> RefreshLoginAsync(long account, string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return TokenPairResult.Fail(AuthErrorType.InvalidCredentials);

        try
        {
            var userIdString = account.ToString();

            // 快速路径：无效令牌时跳过用户查询。真正的互斥由 IssueRefreshTokensAsync 的 CAS 保证。
            var isValid = await _tokenService.ValidateRefreshTokenAsync(userIdString, refreshToken);
            if (!isValid)
                return TokenPairResult.Fail(AuthErrorType.InvalidCredentials);

            // 再回到用户源数据读取最新角色，避免只依赖旧令牌里的历史信息。
            var user = await _db.Users.FindAsync(account);
            if (user is null)
                return TokenPairResult.Fail(AuthErrorType.InvalidCredentials);

            var roles = await GetRolesAsync(user.Id);

            // 原子轮换：CAS 消费旧 RT，并在同一事务中写入新 AT / RT / Session
            var rotated = await _tokenService.IssueRefreshTokensAsync(
                userIdString, refreshToken, user, roles);

            if (rotated is null)
                return TokenPairResult.Fail(AuthErrorType.InvalidCredentials);

            return TokenPairResult.Success(rotated.Value.accessToken, rotated.Value.refreshToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新用户 {UserId} 的令牌时发生异常", account);
            throw new IdentityException("刷新令牌失败", ex);
        }
    }

    /// <summary>
    /// 检查给定的电子邮件地址是否已被注册。
    /// </summary>
    /// <param name="email">要检查的电子邮件地址。</param>
    /// <returns>如果电子邮件已注册，则返回true；否则返回false。在发生异常时也返回false，并记录错误日志。</returns>
    public async Task<bool> IsEmailRegisteredAsync(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        var normalized = email.Trim().ToUpperInvariant();
        return await _db.Users.AnyAsync(u => u.NormalizedEmail == normalized);
    }

    /// <summary>
    /// 记录最后一次成功登录时间，同时将 VerifyUserCredentials 中对 AccessFailedCount / LockoutEnd
    /// 的重置一并持久化，避免重复调用 SaveChangesAsync。
    /// </summary>
    private async Task UpdateLastLoginAsync(ApplicationUser user)
    {
        user.LastLoginDate = DateTimeOffset.UtcNow;
        try { await _db.SaveChangesAsync(); }
        catch (Exception ex)
        {
            // 登录流程已完成，LastLoginDate 更新失败不应阻断响应
            _logger.LogWarning(ex, "用户 {UserId} 登录成功，但最后登录时间更新失败", user.Id);
        }
    }

    private async Task<IList<string>> GetRolesAsync(long userId)
    {
        return await _db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.Role.Name!)
            .ToListAsync();
    }

    /// <summary>
    /// 验证用户凭证的有效性。
    /// </summary>
    /// <param name="account">用户的账号，可以是用户名或电子邮件。</param>
    /// <param name="password">用户提供的密码。</param>
    /// <returns>返回一个元组，包含验证状态和对应的用户对象。如果验证成功，则返回<see cref="LoginCheckStatus.Success"/>及用户对象；
    /// 如果凭证无效，则返回<see cref="LoginCheckStatus.InvalidCredentials"/>且用户对象为null；
    /// 如果账户被锁定，则返回<see cref="LoginCheckStatus.LockedOut"/>且用户对象为null。</returns>
    private async Task<(LoginCheckStatus Status, ApplicationUser? User)> VerifyUserCredentialsAsync(string account,
        string password)
    {
        var normalized = account.Trim().ToUpperInvariant();

        // 邮箱与用户名合并为单次查询，减少一次数据库往返
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.NormalizedEmail == normalized || u.NormalizedUserName == normalized);

        if (user is null)
            return (LoginCheckStatus.InvalidCredentials, null);

        // 锁定检查必须在密码验证之前，避免为已锁定账号执行 BCrypt 计算
        if (user.LockoutEnabled
            && user.LockoutEnd.HasValue
            && user.LockoutEnd.Value > DateTimeOffset.UtcNow)
            return (LoginCheckStatus.LockedOut, null);

        // BCrypt.Verify 是故意慢的 CPU 密集操作，所有前置校验通过后才执行
        var isPasswordValid = !string.IsNullOrEmpty(user.PasswordHash)
                              && _passwordHasher.VerifyPassword(password, user.PasswordHash);

        if (!isPasswordValid)
        {
            user.AccessFailedCount++;

            if (user.LockoutEnabled && user.AccessFailedCount >= MaxFailedAccessAttempts)
            {
                user.LockoutEnd = DateTimeOffset.UtcNow.Add(LockoutDuration);
                _logger.LogWarning("登录失败：连续错误已达上限，账号已锁定。UserId={UserId}", user.Id);
                await _db.SaveChangesAsync();
                return (LoginCheckStatus.LockedOut, null);
            }

            await _db.SaveChangesAsync();
            _logger.LogWarning("登录失败：密码错误。UserId={UserId}, FailedCount={Count}",
                user.Id, user.AccessFailedCount);
            return (LoginCheckStatus.InvalidCredentials, null);
        }

        // 登录成功：重置失败计数，由 UpdateLastLoginAsync 统一提交 SaveChanges
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        return (LoginCheckStatus.Success, user);
    }
}
