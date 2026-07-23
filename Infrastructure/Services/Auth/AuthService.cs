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
using Infrastructure.Services;
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
    ITrustedDeviceService trustedDevices,
    ILoginRiskAnalyzer loginRiskAnalyzer,
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
    public async Task<LoginResult> LoginAsync(
        string account,
        string password,
        string? trustedDeviceToken = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
            return LoginResult.Fail("账号或密码不能为空", LoginCheckStatus.InvalidCredentials);

        try
        {
            var (status, user) = await VerifyUserCredentialsAsync(account, password, cancellationToken);
            if (status == LoginCheckStatus.Overloaded)
                return LoginResult.Fail("服务繁忙，请稍后重试", LoginCheckStatus.Overloaded);
            if (user is null)
            {
                AuthSecurityMetrics.RecordLogin(status == LoginCheckStatus.LockedOut ? "locked" : "invalid");
                return LoginResult.Fail("用户名或密码错误 / 账户已被锁定", status);
            }

            if (user.MustChangePassword)
            {
                AuthSecurityMetrics.RecordLogin("must_change_password");
                return LoginResult.Fail("检测到异常登录，请先重置或修改密码后再登录", LoginCheckStatus.NotAllowed);
            }

            string? rotatedTrustedToken = null;
            if (user.TwoFactorEnabled && !string.IsNullOrWhiteSpace(user.TotpSecret))
            {
                // 仅高熵可信设备令牌可跳过 MFA；绝不信任可伪造的 X-Device-Id。
                if (!string.IsNullOrWhiteSpace(trustedDeviceToken))
                {
                    var (trusted, rotated) = await trustedDevices.ValidateAndRotateAsync(
                        user.Id, trustedDeviceToken, rotate: true, cancellationToken);
                    if (trusted)
                    {
                        _logger.LogInformation("用户 {UserId} 经可信设备令牌跳过 MFA", user.Id);
                        AuthSecurityMetrics.RecordLogin("trusted_skip_mfa");
                        rotatedTrustedToken = rotated;
                        return await CompleteLoginAsync(user, account, cancellationToken, rotatedTrustedToken);
                    }
                }

                var mfaToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var key = CacheKeyBuilder.WithPrefix(CacheConstants.MfaPendingPrefix, mfaToken);
                await cache.StringSetAsync(key, user.Id.ToString(), MfaChallengeTtl, cancellationToken);
                _logger.LogInformation("用户 {UserId} 需要 MFA 验证", user.Id);
                AuthSecurityMetrics.RecordLogin("mfa_required");
                return LoginResult.RequireMfa(user.Id, mfaToken);
            }

            AuthSecurityMetrics.RecordLogin("success");
            return await CompleteLoginAsync(user, account, cancellationToken, rotatedTrustedToken);
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

        var ok = await mfaService.TryVerifyAndConsumeTotpForUserAsync(user, code, cancellationToken)
                     .ConfigureAwait(false)
                 || await mfaService.TryConsumeRecoveryCodeAsync(userId, code, cancellationToken)
                     .ConfigureAwait(false);
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
        AuthSecurityMetrics.RecordLogin("mfa_success");
        var login = await CompleteLoginAsync(
            user, user.UserName ?? user.Email ?? userId.ToString(), cancellationToken);
        if (login.IsSuccess)
        {
            await trustedDevices.MarkRecentMfaAsync(
                userId, login.SessionId, _deviceInfo.GetDeviceId(), cancellationToken);
        }

        return login;
    }

    private async Task<LoginResult> CompleteLoginAsync(
        ApplicationUser user,
        string account,
        CancellationToken cancellationToken,
        string? trustedDeviceToken = null)
    {
        var userIdString = user.Id.ToString();
        var currentDevice = _deviceInfo.GetDeviceId();
        var deviceSnapshot = _deviceInfo.GenerateDeviceInfo();
        var currentIp = deviceSnapshot.IpAddress;

        // 新设备判断：只查当前设备会话，避免每次列举全部会话。
        Core.Models.Token.SessionRecord? currentSession = null;
        if (!string.IsNullOrWhiteSpace(currentDevice))
            currentSession = await _sessionStore.GetSessionAsync(userIdString, currentDevice, cancellationToken);

        var isNewDevice = string.IsNullOrWhiteSpace(currentDevice) || currentSession is null;

        // IP 变化仅为风险信号；异常地点最终判定/通知由 LoginRiskAnalyzer 异步完成，避免重复告警。
        var ipChanged = false;
        if (!string.IsNullOrWhiteSpace(currentIp))
        {
            if (currentSession is not null)
            {
                // 同设备：与该会话上次 IP 比较即可（热路径不扫全量会话）
                ipChanged = string.IsNullOrWhiteSpace(currentSession.ClientIp)
                    || !string.Equals(currentSession.ClientIp, currentIp, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // 新设备：与既有会话 IP 集合比较
                var existingSessions = await _sessionStore.ListSessionsAsync(userIdString, cancellationToken);
                ipChanged = existingSessions.Count > 0
                    && existingSessions.All(s =>
                        string.IsNullOrWhiteSpace(s.ClientIp)
                        || !string.Equals(s.ClientIp, currentIp, StringComparison.OrdinalIgnoreCase));
            }
        }

        var roles = await GetRolesAsync(user.Id, cancellationToken);
        var tokens = await _tokenService.IssueLoginTokensAsync(user, roles, cancellationToken);

        var previousLoginDate = user.LastLoginDate;
        user.LastLoginDate = DateTimeOffset.UtcNow;

        var tcpServer = new ServerEndPoint
        {
            Host = _realtimeGateway.Host,
            Port = _realtimeGateway.Port,
            Name = _realtimeGateway.Name
        };

        _logger.LogInformation(
            "用户 {Username} 登录成功 IsNewDevice={IsNewDevice} IpChanged={IpChanged}",
            account, isNewDevice, ipChanged);

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

        var requiresRecoveryCodeRegeneration =
            HmacRecoveryCodeHasher.ContainsLegacyDigestsStatic(user.RecoveryCodesHashJson);
        if (requiresRecoveryCodeRegeneration)
        {
            loginEvents.Add(new SecurityEvent
            {
                UserId = user.Id,
                EventType = SecurityEventType.MfaRecoveryCodesUpgradeRequired,
                DeviceId = currentDevice,
                ClientIp = currentIp,
                Detail = "legacy-bcrypt-recovery-codes",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            securityNotifications.StageNotify(
                user.Id, "MfaRecoveryCodesUpgrade", "请重新生成恢复码",
                "检测到旧版恢复码格式，请尽快在安全设置中重新生成。", preferEmail: false);
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

        // 地理/ASN 风险分析移出登录热路径；IP 变化仅作为信号传入。
        loginRiskAnalyzer.Enqueue(new LoginRiskWorkItem(
            user.Id, currentIp, currentDevice, isNewDevice, tokens.SessionId, ipChanged));

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
            ipChanged,
            trustedDeviceToken,
            requiresRecoveryCodeRegeneration);
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
            PasswordHash   = await _passwordHasher.HashPasswordAsync(password, cancellationToken),
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

        user.PasswordHash = await _passwordHasher.HashPasswordAsync(newPassword, cancellationToken);
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.MustChangePassword = false;

        await _db.SaveChangesAsync(cancellationToken);
        await _sessionStore.RevokeAllSessionsAsync(user.Id.ToString(), cancellationToken: cancellationToken);
        await trustedDevices.RevokeAllAsync(user.Id, cancellationToken);

        await securityNotifications.NotifyAsync(
            user.Id, "PasswordChanged", "密码已重置",
            "您的账号密码已通过邮箱验证码重置，全部可信设备已失效。", preferEmail: true, cancellationToken);

        _logger.LogInformation("用户 {UserId} 通过邮箱验证码重置密码，已撤销全部会话与可信设备", user.Id);
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

        var isPasswordValid = false;
        try
        {
            isPasswordValid = !string.IsNullOrEmpty(user.PasswordHash)
                              && await _passwordHasher.VerifyPasswordAsync(
                                  password, user.PasswordHash!, cancellationToken);
        }
        catch (PasswordVerifyOverloadedException)
        {
            _logger.LogWarning("登录过载：BCrypt 闸门繁忙，快速拒绝。Account={Account}", account);
            return (LoginCheckStatus.Overloaded, null);
        }

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
