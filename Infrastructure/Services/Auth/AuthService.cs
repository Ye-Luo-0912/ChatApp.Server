using System.Security.Cryptography;
using Core.Caching;
using Core.Exceptions;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Interfaces.Cache;
using Core.Models;
using Core.Models.Auth;
using Core.Models.Identity;
using Core.Models.Security;
using Core.Models.Token;
using Core.Settings;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services.Auth;

/// <summary>
/// 处理登录、登出、注册和令牌续签等认证流程。
/// </summary>
public class AuthService(
    UserDbContext db,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    ISessionStore sessionStore,
    IDeviceInfo deviceInfo,
    IEmailVerificationService emailVerificationService,
    ITsidGenerator tsidGenerator,
    ISecurityEventStore securityEventStore,
    IMfaService mfaService,
    ICacheProvider cache,
    ISecurityNotificationService securityNotifications,
    IOptions<RealtimeGatewayOptions> realtimeGatewayOptions,
    ILogger<AuthService> logger) : IAuthService
{
    private const int MaxFailedAccessAttempts = 5;
    private const int MaxMfaAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MfaChallengeTtl = TimeSpan.FromMinutes(5);

    private readonly ILogger<AuthService> _logger = logger;
    private readonly UserDbContext _db = db;
    private readonly IPasswordHasher _passwordHasher = passwordHasher;
    private readonly ITokenService _tokenService = tokenService;
    private readonly ISessionStore _sessionStore = sessionStore;
    private readonly IDeviceInfo _deviceInfo = deviceInfo;
    private readonly IEmailVerificationService _emailVerificationService = emailVerificationService;
    private readonly RealtimeGatewayOptions _realtimeGateway = realtimeGatewayOptions.Value;

    /// <inheritdoc />
    public async Task<LoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
            return LoginResult.Fail("账号或密码不能为空", LoginCheckStatus.InvalidCredentials);

        try
        {
            var (status, user) = await VerifyUserCredentialsAsync(account, password, cancellationToken);
            if (user is null)
                return LoginResult.Fail("用户名或密码错误 / 账户已被锁定", status);

            if (user.MustChangePassword)
                return LoginResult.Fail("检测到异常登录，请先重置或修改密码后再登录", LoginCheckStatus.NotAllowed);

            if (user.TwoFactorEnabled && !string.IsNullOrWhiteSpace(user.TotpSecret))
            {
                var mfaToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var key = CacheKeyBuilder.WithPrefix(CacheConstants.MfaPendingPrefix, mfaToken);
                await cache.StringSetAsync(key, user.Id.ToString(), MfaChallengeTtl, cancellationToken);
                _logger.LogInformation("用户 {UserId} 需要 MFA 验证", user.Id);
                return LoginResult.RequireMfa(user.Id, mfaToken);
            }

            return await CompleteLoginAsync(user, account, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "用户 {Username} 登录时发生异常", account);
            throw new IdentityException("登录过程中发生错误", ex);
        }
    }

    /// <inheritdoc />
    public async Task<LoginResult> VerifyMfaAsync(
        string mfaToken, string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mfaToken) || string.IsNullOrWhiteSpace(code))
            return LoginResult.Fail("MFA 参数无效", LoginCheckStatus.InvalidCredentials);

        var token = mfaToken.Trim();
        var key = CacheKeyBuilder.WithPrefix(CacheConstants.MfaPendingPrefix, token);
        var attemptsKey = CacheKeyBuilder.WithPrefix(CacheConstants.MfaAttemptsPrefix, token);

        var attempts = await cache.StringIncrementAsync(attemptsKey, MfaChallengeTtl, cancellationToken);
        if (attempts > MaxMfaAttempts)
        {
            await cache.RemoveAsync(key, cancellationToken);
            await cache.RemoveAsync(attemptsKey, cancellationToken);
            return LoginResult.Fail("MFA 尝试次数过多，请重新登录", LoginCheckStatus.NotAllowed);
        }

        var userIdRaw = await cache.StringGetAsync(key, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(userIdRaw) || !long.TryParse(userIdRaw, out var userId))
            return LoginResult.Fail("MFA 挑战已过期，请重新登录", LoginCheckStatus.InvalidCredentials);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || !user.TwoFactorEnabled || string.IsNullOrWhiteSpace(user.TotpSecret))
            return LoginResult.Fail("MFA 不可用", LoginCheckStatus.InvalidCredentials);

        var ok = mfaService.VerifyTotpForUser(user, code)
                 || await mfaService.TryConsumeRecoveryCodeAsync(userId, code, cancellationToken);
        if (!ok)
            return LoginResult.Fail("验证码或恢复码无效", LoginCheckStatus.InvalidCredentials);

        // MFA 成功即视为登录校验通过，清零此前密码失败计数，避免遗留锁定状态。
        if (user.AccessFailedCount != 0 || user.LockoutEnd is not null)
        {
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
        }

        await cache.RemoveAsync(key, cancellationToken);
        await cache.RemoveAsync(attemptsKey, cancellationToken);
        return await CompleteLoginAsync(user, user.UserName ?? user.Email ?? userId.ToString(), cancellationToken);
    }

    private async Task<LoginResult> CompleteLoginAsync(
        ApplicationUser user, string account, CancellationToken cancellationToken)
    {
        var userIdString = user.Id.ToString();
        var existingSessions = await _sessionStore.ListSessionsAsync(userIdString, cancellationToken);
        var currentDevice = _deviceInfo.GetDeviceId();
        var deviceSnapshot = _deviceInfo.GenerateDeviceInfo();
        var currentIp = deviceSnapshot.IpAddress;

        var roles = await GetRolesAsync(user.Id, cancellationToken);
        var tokens = await _tokenService.IssueLoginTokensAsync(user, roles, cancellationToken);

        var previousLoginDate = user.LastLoginDate;
        user.LastLoginDate = DateTimeOffset.UtcNow;

        var isNewDevice = currentDevice is null
            || existingSessions.All(s => !string.Equals(s.DeviceId, currentDevice, StringComparison.Ordinal));
        var isUnusualLocation = !string.IsNullOrWhiteSpace(currentIp)
            && existingSessions.Count > 0
            && existingSessions.All(s =>
                string.IsNullOrWhiteSpace(s.ClientIp)
                || !string.Equals(s.ClientIp, currentIp, StringComparison.OrdinalIgnoreCase));

        var tcpServer = new ServerEndPoint
        {
            Host = _realtimeGateway.Host,
            Port = _realtimeGateway.Port,
            Name = _realtimeGateway.Name
        };

        _logger.LogInformation(
            "用户 {Username} 登录成功 IsNewDevice={IsNewDevice} UnusualLocation={UnusualLocation}",
            account, isNewDevice, isUnusualLocation);

        var loginEvents = new List<SecurityEvent>
        {
            new()
            {
                UserId = user.Id,
                EventType = SecurityEventType.LoginSuccess,
                DeviceId = currentDevice,
                ClientIp = currentIp,
                Detail = $"session={tokens.SessionId}",
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };
        if (isNewDevice)
        {
            loginEvents.Add(new SecurityEvent
            {
                UserId = user.Id,
                EventType = SecurityEventType.LoginNewDevice,
                DeviceId = currentDevice,
                ClientIp = currentIp,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            securityNotifications.StageNotify(
                user.Id, "LoginNewDevice", "新设备登录",
                $"检测到新设备登录，IP：{currentIp ?? "未知"}。", preferEmail: true);
        }

        if (isUnusualLocation)
        {
            loginEvents.Add(new SecurityEvent
            {
                UserId = user.Id,
                EventType = SecurityEventType.LoginUnusualLocation,
                DeviceId = currentDevice,
                ClientIp = currentIp,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            securityNotifications.StageNotify(
                user.Id, "LoginUnusualLocation", "异常地点登录",
                $"检测到与既有会话不一致的 IP：{currentIp ?? "未知"}。", preferEmail: true);
        }

        securityEventStore.StageLoginEvents(loginEvents);
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "用户 {UserId} 登录附属写入失败（LastLogin/安全事件/通知），不影响发令牌", user.Id);
            foreach (var entry in _db.ChangeTracker.Entries()
                         .Where(e => e.State is EntityState.Added or EntityState.Modified)
                         .ToList())
            {
                if (entry.Entity is SecurityEvent or Core.Models.Notifications.NotificationOutboxItem)
                    entry.State = EntityState.Detached;
                else if (entry.Entity is ApplicationUser)
                    entry.State = EntityState.Unchanged;
            }
        }

        return LoginResult.Success(
            user,
            previousLoginDate,
            tokens.SessionId,
            tokens.DeviceIdHash,
            tokens.AccessToken,
            tokens.AccessTokenExpiresAtUtc,
            tokens.RefreshToken,
            tokens.RefreshTokenExpiresAtUtc,
            ref tcpServer,
            currentIp,
            isNewDevice,
            isUnusualLocation);
    }

    public async Task LogoutAsync(long userId, string refreshToken, CancellationToken cancellationToken = default)
    {
        try
        {
            await _tokenService.RevokeRefreshTokenAsync(userId.ToString(), refreshToken, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "撤销刷新令牌失败: {UserId}", userId);
            throw new IdentityException("登出失败", ex);
        }
    }

    public async Task<UserRegistrationResult> RegisterAsync(string? username, string email, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return UserRegistrationResult.Fail([], "账号或者密码不能为空");

        var name       = string.IsNullOrWhiteSpace(username) ? email : username.Trim();
        var normalizedEmail = email.Trim().ToUpperInvariant();
        var normalizedName  = name.ToUpperInvariant();

        if (await _db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken))
            return UserRegistrationResult.Fail([], "该邮箱已被注册");

        if (await _db.Users.AnyAsync(u => u.NormalizedUserName == normalizedName, cancellationToken))
            return UserRegistrationResult.Fail([], "该用户名已被使用");

        var user = new ApplicationUser
        {
            Id                 = tsidGenerator.GenerateTsid(),
            UserName           = name,
            NormalizedUserName = normalizedName,
            Email              = email.Trim(),
            NormalizedEmail    = normalizedEmail,
            EmailConfirmed     = true,
            PasswordHash   = _passwordHasher.HashPassword(password),
            SecurityStamp  = Guid.NewGuid().ToString(),
            LockoutEnabled = true
        };

        try
        {
            _db.Users.Add(user);
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("成功创建用户 {UserId}", user.Id);
            return UserRegistrationResult.Success(user.Id, user.UserName ?? name);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建用户时发生异常");
            throw new IdentityException("用户创建失败", ex);
        }
    }

    public async Task<TokenPairResult> RefreshLoginAsync(long account, string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return TokenPairResult.Fail(AuthErrorType.InvalidCredentials);

        try
        {
            var userIdString = account.ToString();

            var isValid = await _tokenService.ValidateRefreshTokenAsync(userIdString, refreshToken, cancellationToken);
            if (!isValid)
                return TokenPairResult.Fail(AuthErrorType.InvalidCredentials);

            var user = await _db.Users.FindAsync([account], cancellationToken);
            if (user is null)
                return TokenPairResult.Fail(AuthErrorType.InvalidCredentials);

            if (user.MustChangePassword
                || (user.BanUntil.HasValue && user.BanUntil.Value > DateTimeOffset.UtcNow)
                || (user.LockoutEnabled && user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTimeOffset.UtcNow))
                return TokenPairResult.Fail(AuthErrorType.InvalidCredentials);

            var roles = await GetRolesAsync(user.Id, cancellationToken);

            var rotated = await _tokenService.IssueRefreshTokensAsync(
                userIdString, refreshToken, user, roles, cancellationToken);

            if (rotated is null)
                return TokenPairResult.Fail(AuthErrorType.InvalidCredentials);

            return TokenPairResult.Success(rotated.Value.accessToken, rotated.Value.refreshToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新用户 {UserId} 的令牌时发生异常", account);
            throw new IdentityException("刷新令牌失败", ex);
        }
    }

    public async Task<bool> IsEmailRegisteredAsync(string email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        var normalized = email.Trim().ToUpperInvariant();
        return await _db.Users.AnyAsync(u => u.NormalizedEmail == normalized, cancellationToken);
    }

    public async Task<AuthOperationResult> ResetPasswordAsync(
        string email, string code, string newPassword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(newPassword))
            return AuthOperationResult.Fail("ValidationFailed", "邮箱、验证码和新密码均不能为空");

        if (newPassword.Length < 6)
            return AuthOperationResult.Fail("WeakPassword", "密码长度至少 6 位");

        var verify = await _emailVerificationService.VerifyEmailCodeAsync(
            email, code, EmailCodePurpose.ResetPassword, cancellationToken);
        if (!verify.IsSuccess)
            return AuthOperationResult.Fail("InvalidCode", verify.ErrorMessage ?? "验证码无效或已过期");

        var normalized = email.Trim().ToUpperInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        if (user is null)
            return AuthOperationResult.Fail("UserNotFound", "用户不存在");

        user.PasswordHash = _passwordHasher.HashPassword(newPassword);
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.MustChangePassword = false;

        await _db.SaveChangesAsync(cancellationToken);
        await _sessionStore.RevokeAllSessionsAsync(user.Id.ToString(), cancellationToken: cancellationToken);

        await securityNotifications.NotifyAsync(
            user.Id, "PasswordChanged", "密码已重置",
            "您的账号密码已通过邮箱验证码重置。", preferEmail: true, cancellationToken);

        _logger.LogInformation("用户 {UserId} 通过邮箱验证码重置密码，已撤销全部会话", user.Id);
        return AuthOperationResult.Success();
    }

    private async Task<IList<string>> GetRolesAsync(long userId, CancellationToken cancellationToken)
    {
        return await _db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.Role.Name!)
            .ToListAsync(cancellationToken);
    }

    private async Task<(LoginCheckStatus Status, ApplicationUser? User)> VerifyUserCredentialsAsync(string account,
        string password, CancellationToken cancellationToken)
    {
        var normalized = account.Trim().ToUpperInvariant();

        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.NormalizedEmail == normalized || u.NormalizedUserName == normalized, cancellationToken);

        if (user is null)
            return (LoginCheckStatus.InvalidCredentials, null);

        if (user.BanUntil.HasValue && user.BanUntil.Value > DateTimeOffset.UtcNow)
            return (LoginCheckStatus.NotAllowed, null);

        if (user.DeletionScheduledAt.HasValue && user.DeletionScheduledAt.Value <= DateTimeOffset.UtcNow)
            return (LoginCheckStatus.NotAllowed, null);

        if (user.LockoutEnabled
            && user.LockoutEnd.HasValue
            && user.LockoutEnd.Value > DateTimeOffset.UtcNow)
            return (LoginCheckStatus.LockedOut, null);

        var isPasswordValid = !string.IsNullOrEmpty(user.PasswordHash)
                              && _passwordHasher.VerifyPassword(password, user.PasswordHash);

        if (!isPasswordValid)
        {
            user.AccessFailedCount++;

            if (user.LockoutEnabled && user.AccessFailedCount >= MaxFailedAccessAttempts)
            {
                user.LockoutEnd = DateTimeOffset.UtcNow.Add(LockoutDuration);
                _logger.LogWarning("登录失败：连续错误已达上限，账号已锁定。UserId={UserId}", user.Id);
                await _db.SaveChangesAsync(cancellationToken);
                return (LoginCheckStatus.LockedOut, null);
            }

            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogWarning("登录失败：密码错误。UserId={UserId}, FailedCount={Count}",
                user.Id, user.AccessFailedCount);
            return (LoginCheckStatus.InvalidCredentials, null);
        }

        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        return (LoginCheckStatus.Success, user);
    }
}
