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
using Microsoft.Extensions.Logging.Abstractions;
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
    ICacheValueStore cache,
    IAtomicCacheStore atomicCache,
    ISecurityNotificationService securityNotifications,
    ITrustedDeviceService trustedDevices,
    ILoginRiskAnalyzer loginRiskAnalyzer,
    IOptions<RealtimeGatewayOptions> realtimeGatewayOptions,
    ILogger<AuthService> logger,
    IAuthSnapshotStore? authSnapshots = null,
    ISecurityVersionAdvancer? securityVersions = null,
    ISecurityMutationCoordinator? securityMutations = null,
    IOptions<ProfileOptions>? profileOptions = null,
    ISecurityOperationGrantStore? securityOperationGrants = null) : IAuthService
{
    private const int MaxFailedAccessAttempts = 5;
    private const int MaxMfaAttempts = 5;
    private const string LegacyAuthSnapshotKeyPrefix = "auth:snapshot:";
    private static readonly TimeSpan LegacyAuthSnapshotTtl = TimeSpan.FromSeconds(30);
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
    private readonly IAuthSnapshotStore? _authSnapshots = authSnapshots;
    private readonly ISecurityOperationGrantStore? _securityOperationGrants = securityOperationGrants;
    private readonly ProfileOptions _profile = profileOptions?.Value ?? new ProfileOptions();
    private readonly ISecurityMutationCoordinator _securityMutationCoordinator =
        securityMutations ?? new SecurityMutationCoordinator(
            db,
            securityVersions ?? new SecurityVersionAdvancer(db),
            NullLogger<SecurityMutationCoordinator>.Instance);

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

            if (user.TwoFactorEnabled && !string.IsNullOrWhiteSpace(user.TotpSecret))
            {
                // 仅高熵可信设备令牌可跳过 MFA；绝不信任可伪造的 X-Device-Id。
                if (!string.IsNullOrWhiteSpace(trustedDeviceToken))
                {
                    // Validate first. Rotation is deliberately deferred until
                    // the complete login has issued its session and token pair;
                    // a failed token issuance must not consume the only client
                    // copy of the trusted-device credential.
                    var (trusted, _) = await trustedDevices.ValidateAndRotateAsync(
                        user.Id, trustedDeviceToken, rotate: false, cancellationToken);
                    if (trusted)
                    {
                        _logger.LogInformation("用户 {UserId} 经可信设备令牌跳过 MFA", user.Id);
                        AuthSecurityMetrics.RecordLogin("trusted_skip_mfa");
                        var login = await CompleteLoginAsync(user, account, cancellationToken);
                        if (login.IsSuccess)
                        {
                            try
                            {
                                var (_, rotated) = await trustedDevices.ValidateAndRotateAsync(
                                    user.Id, trustedDeviceToken, rotate: true, CancellationToken.None);
                                if (!string.IsNullOrWhiteSpace(rotated))
                                    login.TrustedDeviceToken = rotated;
                            }
                            catch (Exception ex)
                            {
                                // The login result is already authoritative.
                                // The durable security fence and the device
                                // row CAS keep this failure safe.
                                _logger.LogWarning(
                                    ex,
                                    "用户 {UserId} 登录成功但可信设备令牌轮换失败",
                                    user.Id);
                            }
                        }

                        return login;
                    }
                }

                var mfaToken = _securityOperationGrants is null
                    ? TokenBufferEncoding.CreateBase64Url(32)
                    : await _securityOperationGrants
                        .IssueAsync(user.Id, "mfa-login", MfaChallengeTtl, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                var key = CacheKeyBuilder.WithPrefix(CacheConstants.MfaPendingPrefix, mfaToken);
                await cache.StringSetAsync(key, user.Id.ToString(), MfaChallengeTtl, cancellationToken);
                _logger.LogInformation("用户 {UserId} 需要 MFA 验证", user.Id);
                AuthSecurityMetrics.RecordLogin("mfa_required");
                return LoginResult.RequireMfa(user.Id, mfaToken);
            }

            AuthSecurityMetrics.RecordLogin("success");
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
        SecurityOperationGrant? mfaGrant = null;

        var attempts = await atomicCache.StringIncrementAsync(
            attemptsKey, MfaChallengeTtl, cancellationToken);
        if (attempts > MaxMfaAttempts)
        {
            await cache.RemoveAsync(key, cancellationToken);
            await cache.RemoveAsync(attemptsKey, cancellationToken);
            return LoginResult.Fail("MFA 尝试次数过多，请重新登录", LoginCheckStatus.NotAllowed);
        }

        long userId;
        if (_securityOperationGrants is not null)
        {
            mfaGrant = await _securityOperationGrants
                .ClaimAsync(token, "mfa-login", cancellationToken)
                .ConfigureAwait(false);
            if (mfaGrant is null)
                return LoginResult.Fail("MFA 挑战已过期，请重新登录", LoginCheckStatus.InvalidCredentials);

            userId = mfaGrant.UserId;
        }
        else
        {
            var userIdRaw = await cache.StringGetAsync(key, cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(userIdRaw) || !long.TryParse(userIdRaw, out userId))
                return LoginResult.Fail("MFA 挑战已过期，请重新登录", LoginCheckStatus.InvalidCredentials);
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || !user.TwoFactorEnabled || string.IsNullOrWhiteSpace(user.TotpSecret))
        {
            if (mfaGrant is not null)
                await _securityOperationGrants!.RestoreAsync(mfaGrant, CancellationToken.None)
                    .ConfigureAwait(false);
            return LoginResult.Fail("MFA 不可用", LoginCheckStatus.InvalidCredentials);
        }

        var totpClaim = await mfaService.TryClaimTotpForUserAsync(user, code, cancellationToken)
            .ConfigureAwait(false);
        MfaRecoveryCodeClaim? recoveryClaim = null;
        if (totpClaim is null)
        {
            recoveryClaim = await mfaService.TryClaimRecoveryCodeForUserAsync(
                    userId, code, cancellationToken)
                .ConfigureAwait(false);
        }

        if (totpClaim is null && recoveryClaim is null)
        {
            if (mfaGrant is not null)
                await _securityOperationGrants!.RestoreAsync(mfaGrant, CancellationToken.None)
                    .ConfigureAwait(false);
            return LoginResult.Fail("验证码或恢复码无效", LoginCheckStatus.InvalidCredentials);
        }

        var loginIssued = false;
        async Task RestoreClaimsAsync()
        {
            if (mfaGrant is not null)
            {
                try
                {
                    await _securityOperationGrants!.RestoreAsync(mfaGrant, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MFA 安全操作 Grant 恢复失败 UserId={UserId}", userId);
                }
            }

            if (recoveryClaim is not null)
            {
                try
                {
                    await mfaService.RestoreRecoveryCodeClaimAsync(
                            recoveryClaim, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "MFA 恢复码 Claim 恢复失败 UserId={UserId} ClaimId={ClaimId}",
                        userId,
                        recoveryClaim.Id);
                }
            }

            if (totpClaim is not null)
            {
                try
                {
                    await mfaService.RestoreTotpClaimAsync(totpClaim, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MFA TOTP Claim 恢复失败 UserId={UserId}", userId);
                }
            }
        }

        try
        {
            // MFA 成功即视为登录校验通过，清零此前密码失败计数，避免遗留锁定状态。
            // 对恢复码路径，Claim 完成会在同一安全变更事务中保存这些字段并推进版本；
            // TOTP 兼容路径保留原有 fenced 保存顺序。
            if (user.AccessFailedCount != 0 || user.LockoutEnd is not null)
            {
                var hadLockout = user.LockoutEnd is not null;
                var hadFailedCount = user.AccessFailedCount != 0;
                user.AccessFailedCount = 0;
                user.LockoutEnd = null;
                if ((hadLockout || hadFailedCount) && recoveryClaim is null)
                {
                    user.SecurityStamp = Guid.NewGuid().ToString();
                    var ownsTransaction = _db.Database.IsRelational()
                                          && _db.Database.CurrentTransaction is null;
                    await using var transaction = ownsTransaction
                        ? await _db.Database.BeginTransactionAsync(cancellationToken)
                            .ConfigureAwait(false)
                        : null;
                var mutation = await _securityMutationCoordinator.ExecuteAsync(
                        userId,
                        SecurityEventType.LockoutCleared,
                        "mfa-login-lockout-cleared",
                        static _ => Task.CompletedTask,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!mutation.Succeeded)
                {
                        if (transaction is not null)
                            await transaction.RollbackAsync(CancellationToken.None)
                                .ConfigureAwait(false);
                        await RestoreClaimsAsync().ConfigureAwait(false);
                        return LoginResult.Fail(
                            "MFA 状态更新失败，请稍后重试",
                            LoginCheckStatus.Overloaded);
                    }

                    if (transaction is not null)
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (recoveryClaim is not null)
            {
                if (await mfaService.CompleteRecoveryCodeClaimAsync(
                            recoveryClaim, cancellationToken)
                        .ConfigureAwait(false) is null)
                {
                    await RestoreClaimsAsync().ConfigureAwait(false);
                    return LoginResult.Fail(
                        "MFA 状态更新失败，请稍后重试",
                        LoginCheckStatus.Overloaded);
                }
            }

            await cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            await cache.RemoveAsync(attemptsKey, cancellationToken).ConfigureAwait(false);
            AuthSecurityMetrics.RecordLogin("mfa_success");
            var login = await CompleteLoginAsync(
                user, user.UserName ?? user.Email ?? userId.ToString(), cancellationToken);
            if (!login.IsSuccess)
            {
                // The recovery-code completion advanced the durable security
                // version before token issuance so the new session carries
                // the correct fence. If issuance still returns a failed
                // result, restore the claim instead of burning the code.
                await RestoreClaimsAsync().ConfigureAwait(false);
                return login;
            }
            loginIssued = true;

            try
            {
                await trustedDevices.MarkRecentMfaAsync(
                    userId, login.SessionId, _deviceInfo.GetDeviceId(), cancellationToken);
            }
            catch (Exception ex)
            {
                // Recent-MFA is an optimization for subsequent step-up. It
                // must never turn an already-issued login into HTTP 500.
                _logger.LogWarning(ex, "MFA 登录成功但最近 MFA 标记写入失败 UserId={UserId}", userId);
            }

            if (mfaGrant is not null)
            {
                try
                {
                    if (!await _securityOperationGrants!.CompleteAsync(
                                mfaGrant,
                                CancellationToken.None)
                            .ConfigureAwait(false))
                    {
                        _logger.LogWarning("MFA 登录成功但安全操作 Grant 未完成 UserId={UserId}", userId);
                    }
                }
                catch (Exception ex)
                {
                    // Login is already authoritative; an incomplete grant is
                    // bounded by its expiry and can never be replayed after
                    // this claimed transition.
                    _logger.LogWarning(ex, "MFA 登录成功但安全操作 Grant 完成失败 UserId={UserId}", userId);
                }
            }

            return login;
        }
        catch
        {
            // A completed recovery claim is still reversible until the login
            // pair has been successfully issued. This covers exceptions from
            // token/session creation after the claim transaction committed.
            if (!loginIssued)
                await RestoreClaimsAsync().ConfigureAwait(false);
            throw;
        }
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

        var roles = await GetRolesAsync(user, cancellationToken);
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
                SessionId = tokens.SessionId,
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
                SessionId = tokens.SessionId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            securityNotifications.StageNotify(
                user.Id, "LoginNewDevice", "新设备登录",
                $"检测到新设备登录，IP 网段：{IpPrivacy.Display(currentIp)}。", preferEmail: true);
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
        // Persist the lightweight risk signal in the same SaveChanges unit as
        // the login audit rows. No API-local bounded queue can silently drop a
        // signal during a burst or process restart.
        loginRiskAnalyzer.Enqueue(new LoginRiskWorkItem(
            user.Id, currentIp, currentDevice, isNewDevice, tokens.SessionId, ipChanged));
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var riskWasStaged = _db.ChangeTracker.Entries<LoginRiskOutboxItem>()
                .Any(e => e.State is EntityState.Added or EntityState.Modified);
            if (riskWasStaged)
            {
                AuthSecurityMetrics.RecordLoginRiskDropped();
                try
                {
                    // The token pair was provisioned before the login audit
                    // transaction. A durable risk outbox is part of the
                    // successful-login boundary, so do not return a pair when
                    // that boundary failed. Cleanup is best effort because the
                    // original cache failure may be the cause of this error.
                    await _tokenService.RevokeRefreshTokenAsync(
                            user.Id.ToString(),
                            tokens.RefreshToken,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(
                        cleanupEx,
                        "登录附属写入失败后撤销临时会话失败 UserId={UserId} SessionId={SessionId}",
                        user.Id,
                        tokens.SessionId);
                }

                throw new IdentityException("登录安全记录暂时不可用，请稍后重试", ex);
            }

            _logger.LogWarning(ex, "用户 {UserId} 登录附属写入失败（LastLogin/安全事件/通知），不影响发令牌", user.Id);
            foreach (var entry in _db.ChangeTracker.Entries()
                         .Where(e => e.State is EntityState.Added or EntityState.Modified)
                         .ToList())
            {
                if (entry.Entity is SecurityEvent
                    or LoginAuditOutboxItem
                    or LoginRiskOutboxItem
                    or Core.Models.Notifications.NotificationOutboxItem)
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
            ipChanged,
            trustedDeviceToken,
            requiresRecoveryCodeRegeneration,
            tokens.DeviceCredential);
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

        var explicitName = !string.IsNullOrWhiteSpace(username);
        var name = explicitName ? username!.Trim() : email;
        if (explicitName
            && (name.Length < _profile.UserNameMinLength
                || name.Length > _profile.UserNameMaxLength
                || !IsValidUserNameCharacters(name)))
            return UserRegistrationResult.Fail([], "用户名长度或格式不符合要求");
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
            PasswordHashVersion = _passwordHasher.CurrentHashVersion,
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

            // P0-3.2：移除冗余的 ValidateRefreshTokenAsync 调用。
            // 旧实现先 GET 验证，再 CAS 消费同一 key——第一次验证不能替代后面的 CAS，
            // 只增加一次 Redis 往返，且两次调用之间存在竞态（验证通过后可能被另一请求消费）。
            // IssueRefreshTokensAsync 内部的 TryAtomicConsumeAsync 已原子地验证 + 轮换，
            // 失败返回 null。先查用户状态再轮换，避免无效令牌触发 Redis 写。
            var user = await _db.Users.FindAsync([account], cancellationToken);
            if (user is null)
                return TokenPairResult.Fail(AuthErrorType.InvalidCredentials);

            if (user.MustChangePassword
                || (user.BanUntil.HasValue && user.BanUntil.Value > DateTimeOffset.UtcNow)
                || (user.LockoutEnabled && user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTimeOffset.UtcNow)
                || (user.DeletionScheduledAt.HasValue
                    && user.DeletionScheduledAt.Value <= DateTimeOffset.UtcNow)
                || user.AccountState == AccountState.Deleted)
                return TokenPairResult.Fail(AuthErrorType.InvalidCredentials);

            var roles = await GetRolesAsync(user, cancellationToken);

            var rotated = await _tokenService.IssueRefreshTokensAsync(
                userIdString, refreshToken, user, roles, cancellationToken);

            if (rotated is null)
                return TokenPairResult.Fail(AuthErrorType.InvalidCredentials);

            return TokenPairResult.Success(
                rotated.Value.AccessToken,
                rotated.Value.AccessTokenExpiresAtUtc,
                rotated.Value.RefreshToken,
                rotated.Value.RefreshTokenExpiresAtUtc,
                rotated.Value.DeviceCredential);
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

        var (verify, emailClaim) = await _emailVerificationService.ClaimEmailCodeAsync(
            email, code, EmailCodePurpose.ResetPassword, cancellationToken);
        if (!verify.IsSuccess)
            return AuthOperationResult.Fail("InvalidCode", verify.ErrorMessage ?? "验证码无效或已过期");

        var committed = false;
        try
        {
            var normalized = email.Trim().ToUpperInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(
                u => u.NormalizedEmail == normalized, cancellationToken);
            if (user is null)
            {
                await _emailVerificationService.RestoreEmailCodeAsync(emailClaim!, cancellationToken);
                return AuthOperationResult.Fail("UserNotFound", "用户不存在");
            }

            user.PasswordHash = await _passwordHasher.HashPasswordAsync(newPassword, cancellationToken);
            user.PasswordHashVersion = _passwordHasher.CurrentHashVersion;
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
            user.MustChangePassword = false;

            var ownsTransaction = _db.Database.IsRelational()
                                  && _db.Database.CurrentTransaction is null;
            await using var transaction = ownsTransaction
                ? await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                : null;
            var mutation = await _securityMutationCoordinator.ExecuteAsync(
                    user.Id,
                    SecurityEventType.PasswordChanged,
                    "password-reset",
                    static _ => Task.CompletedTask,
                    cancellationToken,
                    options: new SecurityMutationOptions(RevokeTrustedDevices: true))
                .ConfigureAwait(false);
            if (!mutation.Succeeded)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                await _emailVerificationService.RestoreEmailCodeAsync(emailClaim!, cancellationToken);
                return AuthOperationResult.Fail("UpdateFailed", "用户安全版本无法推进");
            }
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;

            try
            {
                await _emailVerificationService.CompleteEmailCodeAsync(
                    emailClaim!, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "密码重置已提交但验证码完成清理失败 UserId={UserId}", user.Id);
            }

            try
            {
                await _sessionStore.RevokeAllSessionsAsync(
                        user.Id.ToString(),
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "密码重置后的会话清理暂不可用 UserId={UserId}", user.Id);
            }

            try
            {
                await trustedDevices.RevokeAllAsync(user.Id, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // RevokeTrustedDevices is also carried by the committed
                // security revocation outbox; this call is only the low
                // latency best-effort path.
                _logger.LogWarning(ex, "密码重置后的可信设备清理暂不可用 UserId={UserId}", user.Id);
            }

            await securityNotifications.NotifyAsync(
                user.Id, "PasswordChanged", "密码已重置",
                "您的账号密码已通过邮箱验证码重置，全部可信设备已失效。", preferEmail: true, cancellationToken);

            _logger.LogInformation("用户 {UserId} 通过邮箱验证码重置密码，已撤销全部会话与可信设备", user.Id);
            return AuthOperationResult.Success();
        }
        catch
        {
            if (!committed)
            {
                try
                {
                    await _emailVerificationService.RestoreEmailCodeAsync(
                        emailClaim!, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception restoreError)
                {
                    _logger.LogWarning(restoreError, "密码重置失败后恢复邮箱验证码失败");
                }
            }
            throw;
        }
    }

    private async Task<IList<string>> GetRolesAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        if (_authSnapshots is not null)
        {
            var cached = await _authSnapshots.GetAsync(user.Id, cancellationToken)
                .ConfigureAwait(false);
            if (cached is not null && cached.SecurityVersion == user.SecurityVersion)
                return cached.Roles;
        }
        else
        {
            try
            {
                var cached = await cache.GetAsync<UserAuthSnapshot>(
                        LegacyAuthSnapshotKeyPrefix + user.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (cached is not null
                    && cached.UserId == user.Id
                    && cached.SecurityVersion == user.SecurityVersion)
                    return cached.Roles;
            }
            catch (CacheUnavailableException)
            {
                // Legacy direct-construction test path only.
            }
        }

        var roles = await _db.UserRoles
            .Where(ur => ur.UserId == user.Id)
            .Select(ur => ur.Role.Name!)
            .ToListAsync(cancellationToken);

        var snapshot = CreateSnapshot(user, [.. roles]);
        if (_authSnapshots is not null)
        {
            await _authSnapshots.SetAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                await cache.SetAsync(
                        LegacyAuthSnapshotKeyPrefix + user.Id,
                        snapshot,
                        LegacyAuthSnapshotTtl,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CacheUnavailableException)
            {
                // Legacy direct-construction test path only.
            }
        }

        return roles;
    }

    private static bool IsValidUserNameCharacters(string value)
    {
        foreach (var ch in value)
        {
            if (!((ch is >= 'a' and <= 'z')
                  || (ch is >= 'A' and <= 'Z')
                  || (ch is >= '0' and <= '9')
                  || ch == '_'))
                return false;
        }

        return value.Length > 0;
    }

    private static UserAuthSnapshot CreateSnapshot(
        ApplicationUser user,
        string[] roles) => new()
        {
            UserId = user.Id,
            UserName = user.UserName,
            SecurityVersion = user.SecurityVersion,
            AccountState = user.DeletionScheduledAt is { } scheduledAt
                             && scheduledAt > DateTimeOffset.UtcNow
                ? AccountState.DeletionPending
                : user.AccountState,
            Roles = roles,
            RolesLoaded = true,
            LockoutEnabled = user.LockoutEnabled,
            LockoutEnd = user.LockoutEnd,
            BanUntil = user.BanUntil,
            DeletionScheduledAt = user.DeletionScheduledAt,
        };

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

        if (user.AccountState == AccountState.Deleted
            || (user.DeletionScheduledAt.HasValue
                && user.DeletionScheduledAt.Value <= DateTimeOffset.UtcNow))
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
            // P0-3.1：原子递增 AccessFailedCount。
            // 旧实现 user.AccessFailedCount++ + SaveChanges 是 read-modify-write，
            // 并发失败请求会读取相同旧值并互相覆盖（5 个并发失败可能只写成 1）。
            // ExecuteUpdateAsync 生成 UPDATE "AspNetUsers" SET "AccessFailedCount" = "AccessFailedCount" + 1，
            // 数据库层面原子，不会丢失增量。
            await _db.Users
                .Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(u => u.AccessFailedCount, u => u.AccessFailedCount + 1),
                    cancellationToken);

            // 读取递增后的值用于锁定判定。投影查询绕过 identity map，直接读 DB。
            // 即使并发递增，读到的值 >= 本次增量（单调），锁定判定安全。
            var failedState = await _db.Users
                .Where(u => u.Id == user.Id)
                .Select(u => new { u.AccessFailedCount, u.LockoutEnabled })
                .FirstAsync(cancellationToken);

            if (failedState.LockoutEnabled && failedState.AccessFailedCount >= MaxFailedAccessAttempts)
            {
                var lockoutEnd = DateTimeOffset.UtcNow.Add(LockoutDuration);
                var ownsTransaction = _db.Database.IsRelational()
                                      && _db.Database.CurrentTransaction is null;
                await using var transaction = ownsTransaction
                    ? await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                    : null;
                var lockoutRows = await _db.Users
                    .Where(u => u.Id == user.Id
                                && (u.LockoutEnd == null || u.LockoutEnd <= DateTimeOffset.UtcNow))
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(u => u.LockoutEnd, lockoutEnd)
                            .SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString()),
                        cancellationToken);
                if (lockoutRows == 1)
                {
                    var mutation = await _securityMutationCoordinator.ExecuteAsync(
                            user.Id,
                            SecurityEventType.AccountLocked,
                            "password-lockout",
                            static _ => Task.CompletedTask,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!mutation.Succeeded)
                    {
                        if (transaction is not null)
                            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        return (LoginCheckStatus.Overloaded, null);
                    }

                    if (transaction is not null)
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                _logger.LogWarning("登录失败：连续错误已达上限，账号已锁定。UserId={UserId}", user.Id);
                return (LoginCheckStatus.LockedOut, null);
            }

            _logger.LogWarning("登录失败：密码错误。UserId={UserId}, FailedCount={Count}",
                user.Id, failedState.AccessFailedCount);
            return (LoginCheckStatus.InvalidCredentials, null);
        }

        if (!string.IsNullOrWhiteSpace(user.PasswordHash)
            && _passwordHasher.NeedsRehash(user.PasswordHash, user.PasswordHashVersion))
        {
            // Keep the plaintext only for this verification call. If the
            // later login persistence fails, the old version remains and the
            // next successful login retries safely.
            user.PasswordHash = await _passwordHasher
                .HashPasswordAsync(password, cancellationToken)
                .ConfigureAwait(false);
            user.PasswordHashVersion = _passwordHasher.CurrentHashVersion;
        }

        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        return (LoginCheckStatus.Success, user);
    }
}
