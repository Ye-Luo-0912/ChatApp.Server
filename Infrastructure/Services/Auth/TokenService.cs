using System.Security.Cryptography;
using System.Text;
using Core.Caching;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Interfaces.Cache;
using Core.Models.Identity;
using Core.Models.Token;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services.Auth;

/// <summary>
/// 令牌服务：<see cref="ITokenService"/> 的唯一实现，替代原 JwtTokenServices。
/// <para>
/// 职责拆分：
/// <list type="bullet">
///   <item><b>ITokenGenerator</b>：使用 <see cref="RandomNumberGenerator"/> 生成 URL 安全的随机字符串，不再依赖 JWT 库。</item>
///   <item><b>IAccessTokenStore</b>：将访问令牌元数据（UserId、Roles）写入 Redis，由认证中间件读取以构建 ClaimsPrincipal。</item>
///   <item><b>IRefreshTokenStore</b>：管理刷新令牌生命周期（存储、校验、撤销、轮换），绑定设备指纹，防止跨设备复用。</item>
///   <item><b>ISessionStore</b>：维护会话记录（设备管理、TCP 关联）；由刷新令牌写入路径自动更新。</item>
/// </list>
/// </para>
/// <para>
/// 由于两种令牌均为不透明随机字符串（Opaque Token），服务端完全掌控令牌状态，支持即时撤销。
/// </para>
/// </summary>
public sealed class TokenService(
    ICacheProvider cache,
    IDeviceInfo deviceInfo,
    IOptions<JwtSettings> options,
    ILogger<TokenService> logger) : ITokenService
{
    // Redis 键前缀
    private const string AccessTokenPrefix  = "AT:";
    private const string RefreshTokenPrefix = "RT:";
    private const string SessionPrefix      = "SS:";

    private readonly JwtSettings _settings = options.Value;

    // ─────────────────────────────────────────────────────────────────────────
    // ITokenGenerator
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 生成 URL 安全的随机令牌字符串（Base64url 编码，无填充）。
    /// </summary>
    public string Generate(int byteLength = 16)
    {
        var bytes = new byte[byteLength];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IAccessTokenStore
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 将访问令牌元数据写入 Redis，键为 AT:{SHA-256(token)}，防止原始令牌暴露在日志中。
    /// </summary>
    public Task StoreAccessTokenAsync(string token, AccessTokenData data, TimeSpan expiry)
        => cache.SetAsync(AccessTokenKey(token), data, absoluteExpiration: expiry);

    /// <summary>
    /// 查询访问令牌对应的元数据，不存在则返回 <see langword="null"/>。
    /// </summary>
    public Task<AccessTokenData?> GetAccessTokenAsync(string token)
        => cache.GetAsync<AccessTokenData>(AccessTokenKey(token));

    /// <summary>
    /// 从 Redis 中删除访问令牌（主动登出或安全事件触发的强制下线）。
    /// </summary>
    public Task RevokeAccessTokenAsync(string token)
        => cache.RemoveAsync(AccessTokenKey(token));

    // ─────────────────────────────────────────────────────────────────────────
    // IRefreshTokenStore
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 将刷新令牌及当前请求的设备信息写入 Redis，同时更新对应的 <see cref="SessionRecord"/>。
    /// </summary>
    public async Task StoreRefreshTokenAsync(string userId, string refreshToken)
    {
        var device = deviceInfo.GenerateDeviceInfo();
        var expiry = TimeSpan.FromDays(_settings.RefreshTokenExpirationDays);
        var rtKey  = RefreshTokenKey(userId, refreshToken);
        var ssKey  = SessionKey(userId, device.DeviceId);
        var now    = DateTime.UtcNow;

        var rtData = new RefreshToken
        {
            DeviceId    = device.DeviceId,
            ExpiresAtMs = new DateTimeOffset(now.Add(expiry)).ToUnixTimeMilliseconds(),
            LoginAt     = now,
        };

        var session = new SessionRecord
        {
            UserId       = userId,
            DeviceId     = device.DeviceId,
            DeviceName   = device.DeviceName,
            DeviceType   = device.DeviceType,
            ClientIp     = device.IpAddress,
            UserAgent    = device.UserAgent,
            LoginAt      = now,
            LastActiveAt = now,
            ExpiresAt    = now.Add(expiry),
        };

        await Task.WhenAll(
            cache.SetAsync(rtKey, rtData,  absoluteExpiration: expiry),
            cache.SetAsync(ssKey, session, absoluteExpiration: expiry));

        logger.LogDebug("刷新令牌及会话已写入缓存，UserId={UserId}, DeviceId={DeviceId}", userId, device.DeviceId);
    }

    /// <summary>
    /// 验证刷新令牌：Redis 中存在 + 未过期 + 设备 ID 与当前请求一致。
    /// </summary>
    public async Task<bool> ValidateRefreshTokenAsync(string userId, string refreshToken)
    {
        var data     = await GetRefreshTokenData(userId, refreshToken);
        var deviceId = deviceInfo.GetDeviceId();

        if (data is null || !data.IsValid)
            return false;

        // 确认令牌来自同一设备，防止跨设备复用
        return deviceId is not null && data.DeviceId == deviceId;
    }

    /// <summary>
    /// 从 Redis 中删除指定刷新令牌，并同步撤销对应的会话记录（主动登出）。
    /// </summary>
    public async Task RevokeRefreshTokenAsync(string userId, string refreshToken)
    {
        // 先读取设备 ID，以便同步删除会话记录
        var data = await GetRefreshTokenData(userId, refreshToken);
        await cache.RemoveAsync(RefreshTokenKey(userId, refreshToken));

        if (data?.DeviceId is { } deviceId)
            await cache.RemoveAsync(SessionKey(userId, deviceId));
    }

    /// <summary>
    /// 查询刷新令牌元数据，不存在则返回 <see langword="null"/>。
    /// </summary>
    public Task<RefreshToken?> GetRefreshTokenAsync(string userId, string refreshToken)
        => GetRefreshTokenData(userId, refreshToken);

    /// <summary>
    /// 先校验令牌（含设备匹配），通过后立即原子撤销——一次性消费语义。
    /// </summary>
    public async Task<bool> ValidateAndRevokeRefreshTokenAsync(string userId, string refreshToken)
    {
        var deviceId = deviceInfo.GetDeviceId();
        if (deviceId is null)
            return false;

        var consumed = await cache.TryAtomicConsumeAsync<RefreshToken, bool>(
            RefreshTokenKey(userId, refreshToken),
            oldRt =>
            {
                if (!oldRt.IsValid || oldRt.DeviceId != deviceId)
                    return null;

                return new AtomicConsumePlan<bool>
                {
                    Result = true,
                    AdditionalKeysToDelete = [SessionKey(userId, oldRt.DeviceId)],
                };
            });

        return consumed.Succeeded;
    }

    /// <summary>
    /// 原子地撤销旧刷新令牌并写入新刷新令牌，继承原始登录时间并递增轮换计数。
    /// <para>同时撤销旧 RT 对应的访问令牌（通过 <see cref="RefreshToken.CurrentAccessTokenKey"/>)。</para>
    /// </summary>
    public async Task<bool> RotateRefreshTokenAsync(string userId, string oldRefreshToken, string newRefreshToken)
    {
        var device = deviceInfo.GenerateDeviceInfo();
        var expiry = TimeSpan.FromDays(_settings.RefreshTokenExpirationDays);
        var rtKey  = RefreshTokenKey(userId, newRefreshToken);
        var ssKey  = SessionKey(userId, device.DeviceId);
        var now    = DateTime.UtcNow;

        var rotated = await cache.TryAtomicConsumeAsync<RefreshToken, bool>(
            RefreshTokenKey(userId, oldRefreshToken),
            oldRt =>
            {
                if (!oldRt.IsValid || oldRt.DeviceId != device.DeviceId)
                    return null;

                var count   = oldRt.RefreshCount + 1;
                var login   = oldRt.LoginAt == default ? now : oldRt.LoginAt;
                var session = oldRt.SessionId;
                var deletes = new List<string>(1);
                if (oldRt.CurrentAccessTokenKey is { } oldAtKey)
                    deletes.Add(oldAtKey);

                var newRtData = new RefreshToken
                {
                    DeviceId     = device.DeviceId,
                    ExpiresAtMs  = new DateTimeOffset(now.Add(expiry)).ToUnixTimeMilliseconds(),
                    LoginAt      = login,
                    RefreshCount = count,
                    SessionId    = session,
                };

                var newSession = new SessionRecord
                {
                    UserId                 = userId,
                    SessionId              = session,
                    DeviceId               = device.DeviceId,
                    DeviceName             = device.DeviceName,
                    DeviceType             = device.DeviceType,
                    ClientIp               = device.IpAddress,
                    UserAgent              = device.UserAgent,
                    LoginAt                = login,
                    LastActiveAt           = now,
                    ExpiresAt              = now.Add(expiry),
                    RefreshCount           = count,
                    CurrentRefreshTokenKey = rtKey,
                };

                return new AtomicConsumePlan<bool>
                {
                    Result = true,
                    AdditionalKeysToDelete = deletes,
                    Writes =
                    [
                        new CacheSetRequest { Key = rtKey, Value = newRtData, AbsoluteExpiration = expiry },
                        new CacheSetRequest { Key = ssKey, Value = newSession, AbsoluteExpiration = expiry },
                    ],
                };
            });

        if (rotated.Succeeded)
        {
            logger.LogDebug("刷新令牌已轮换，UserId={UserId}, DeviceId={DeviceId}",
                userId, device.DeviceId);
        }

        return rotated.Succeeded;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IJwtTokenService — 高阶业务方法
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 登录时一次性签发访问令牌和刷新令牌，并将两者持久化到 Redis。
    /// 生成 SessionId 并建立 AT / RT / Session 三者间的第一方关联。
    /// </summary>
    public async Task<TokenIssueResult> IssueLoginTokensAsync(ApplicationUser user, IList<string> roles)
    {
        var sessionId     = Generate(16);
        var rawAt         = Generate(16);
        var rawRt         = Generate(_settings.RefreshTokenLength);
        var userId        = user.Id.ToString();
        var device        = deviceInfo.GenerateDeviceInfo();
        var now           = DateTime.UtcNow;
        var accessExpiry  = TimeSpan.FromMinutes(_settings.AccessTokenExpirationMinutes);
        var refreshExpiry = TimeSpan.FromDays(_settings.RefreshTokenExpirationDays);
        var atKey         = AccessTokenKey(rawAt);
        var rtKey         = RefreshTokenKey(userId, rawRt);
        var ssKey         = SessionKey(userId, device.DeviceId);

        var atData = new AccessTokenData
        {
            UserId       = user.Id,
            UserName     = user.UserName ?? string.Empty,
            Roles        = roles.Count > 0 ? [.. roles] : null,
            ExpiresAtMs  = DateTimeOffset.UtcNow.Add(accessExpiry).ToUnixTimeMilliseconds(),
            SessionId    = sessionId,
            DeviceIdHash = DeviceIdHashHelper.Compute(device.DeviceId),
        };

        var rtData = new RefreshToken
        {
            DeviceId              = device.DeviceId,
            ExpiresAtMs           = new DateTimeOffset(now.Add(refreshExpiry)).ToUnixTimeMilliseconds(),
            LoginAt               = now,
            SessionId             = sessionId,
            CurrentAccessTokenKey = atKey,
        };

        var session = new SessionRecord
        {
            UserId                  = userId,
            SessionId               = sessionId,
            DeviceId                = device.DeviceId,
            DeviceName              = device.DeviceName,
            DeviceType              = device.DeviceType,
            ClientIp                = device.IpAddress,
            UserAgent               = device.UserAgent,
            LoginAt                 = now,
            LastActiveAt            = now,
            ExpiresAt               = now.Add(refreshExpiry),
            CurrentAccessTokenKey   = atKey,
            CurrentRefreshTokenKey  = rtKey,
        };

        await Task.WhenAll(
            cache.SetAsync(atKey, atData,   absoluteExpiration: accessExpiry),
            cache.SetAsync(rtKey, rtData,   absoluteExpiration: refreshExpiry),
            cache.SetAsync(ssKey, session,  absoluteExpiration: refreshExpiry));

        logger.LogDebug("登录令牌已建立，UserId={UserId}, SessionId={SessionId}, DeviceId={DeviceId}",
            userId, sessionId, device.DeviceId);

        return new TokenIssueResult
        {
            AccessToken              = rawAt,
            AccessTokenExpiresAtUtc  = now.Add(accessExpiry),
            RefreshToken             = rawRt,
            RefreshTokenExpiresAtUtc = now.Add(refreshExpiry),
            SessionId                = sessionId,
            DeviceIdHash             = DeviceIdHashHelper.Compute(device.DeviceId),
        };
    }

    /// <summary>
    /// 签发并存储一枚访问令牌（最小载荷，仅含认证必需字段）。
    /// </summary>
    public async Task<string> IssueAccessTokenAsync(ApplicationUser user, IList<string> roles, string? sessionId = null)
    {
        var token  = Generate(16);
        var expiry = TimeSpan.FromMinutes(_settings.AccessTokenExpirationMinutes);

        var data = new AccessTokenData
        {
            UserId      = user.Id,
            UserName    = user.UserName ?? string.Empty,
            Roles       = roles.Count > 0 ? [.. roles] : null,
            ExpiresAtMs = DateTimeOffset.UtcNow.Add(expiry).ToUnixTimeMilliseconds(),
            SessionId   = sessionId,
            DeviceIdHash    = DeviceIdHashHelper.Compute(deviceInfo.GetDeviceId()),
        };

        await StoreAccessTokenAsync(token, data, expiry);
        return token;
    }

    /// <summary>
    /// 原子地完成令牌轮换：CAS 消费旧 RT，并在同一事务中写入新 AT / RT / Session。
    /// </summary>
    public async Task<(string accessToken, string refreshToken)?> IssueRefreshTokensAsync(
        string userId, string oldRefreshToken, ApplicationUser user, IList<string> roles)
    {
        var device = deviceInfo.GenerateDeviceInfo();
        var now    = DateTime.UtcNow;
        var accessExpiry  = TimeSpan.FromMinutes(_settings.AccessTokenExpirationMinutes);
        var refreshExpiry = TimeSpan.FromDays(_settings.RefreshTokenExpirationDays);

        // 在 CAS 回调外预生成令牌字符串，避免重试/并发路径产生不一致的返回值。
        var rawAt = Generate(16);
        var rawRt = Generate(_settings.RefreshTokenLength);
        var atKey = AccessTokenKey(rawAt);
        var rtKey = RefreshTokenKey(userId, rawRt);
        var ssKey = SessionKey(userId, device.DeviceId);

        var rotated = await cache.TryAtomicConsumeAsync<RefreshToken, (string accessToken, string refreshToken)>(
            RefreshTokenKey(userId, oldRefreshToken),
            oldRt =>
            {
                if (!oldRt.IsValid || oldRt.DeviceId != device.DeviceId)
                    return null;

                var sessionId = oldRt.SessionId ?? Generate(16);
                var count     = oldRt.RefreshCount + 1;
                var login     = oldRt.LoginAt == default ? now : oldRt.LoginAt;

                var deletes = new List<string>(1);
                if (oldRt.CurrentAccessTokenKey is { } oldAtKey)
                    deletes.Add(oldAtKey);

                var atData = new AccessTokenData
                {
                    UserId       = user.Id,
                    UserName     = user.UserName ?? string.Empty,
                    Roles        = roles.Count > 0 ? [.. roles] : null,
                    ExpiresAtMs  = DateTimeOffset.UtcNow.Add(accessExpiry).ToUnixTimeMilliseconds(),
                    SessionId    = sessionId,
                    DeviceIdHash = DeviceIdHashHelper.Compute(device.DeviceId),
                };

                var newRtData = new RefreshToken
                {
                    DeviceId              = device.DeviceId,
                    ExpiresAtMs           = new DateTimeOffset(now.Add(refreshExpiry)).ToUnixTimeMilliseconds(),
                    LoginAt               = login,
                    RefreshCount          = count,
                    SessionId             = sessionId,
                    CurrentAccessTokenKey = atKey,
                };

                var session = new SessionRecord
                {
                    UserId                 = userId,
                    SessionId              = sessionId,
                    DeviceId               = device.DeviceId,
                    DeviceName             = device.DeviceName,
                    DeviceType             = device.DeviceType,
                    ClientIp               = device.IpAddress,
                    UserAgent              = device.UserAgent,
                    LoginAt                = login,
                    LastActiveAt           = now,
                    ExpiresAt              = now.Add(refreshExpiry),
                    RefreshCount           = count,
                    CurrentAccessTokenKey  = atKey,
                    CurrentRefreshTokenKey = rtKey,
                };

                return new AtomicConsumePlan<(string accessToken, string refreshToken)>
                {
                    Result = (rawAt, rawRt),
                    AdditionalKeysToDelete = deletes,
                    Writes =
                    [
                        new CacheSetRequest { Key = atKey, Value = atData, AbsoluteExpiration = accessExpiry },
                        new CacheSetRequest { Key = rtKey, Value = newRtData, AbsoluteExpiration = refreshExpiry },
                        new CacheSetRequest { Key = ssKey, Value = session, AbsoluteExpiration = refreshExpiry },
                    ],
                };
            });

        if (!rotated.Succeeded)
        {
            logger.LogDebug("令牌轮换失败（无效或已被并发消费），UserId={UserId}", userId);
            return null;
        }

        logger.LogDebug("令牌已轮换，UserId={UserId}, DeviceId={DeviceId}", userId, device.DeviceId);
        return rotated.Value;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ISessionStore
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 查询指定用户在指定设备上的会话记录；不存在则返回 <see langword="null"/>。
    /// </summary>
    public Task<SessionRecord?> GetSessionAsync(string userId, string deviceId)
        => cache.GetAsync<SessionRecord>(SessionKey(userId, deviceId));

    /// <summary>
    /// 撤销（删除）指定用户在指定设备上的会话记录，并同步删除对应的访问令牌和刷新令牌。
    /// </summary>
    public async Task RevokeSessionAsync(string userId, string deviceId)
    {
        var session = await GetSessionAsync(userId, deviceId);
        if (session is null) return;

        var tasks = new List<Task>(3) { cache.RemoveAsync(SessionKey(userId, deviceId)) };
        if (session.CurrentAccessTokenKey  is not null) tasks.Add(cache.RemoveAsync(session.CurrentAccessTokenKey));
        if (session.CurrentRefreshTokenKey is not null) tasks.Add(cache.RemoveAsync(session.CurrentRefreshTokenKey));

        await Task.WhenAll(tasks);
        logger.LogDebug("会话已撤销，UserId={UserId}, DeviceId={DeviceId}", userId, deviceId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 私有辅助方法
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>对令牌做 SHA-256 哈希，用于构造 Redis 键，避免原始值出现在键名中。</summary>
    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    private static string AccessTokenKey(string token)
        => $"{AccessTokenPrefix}{HashToken(token)}";

    private static string RefreshTokenKey(string userId, string token)
        => $"{RefreshTokenPrefix}{userId}:{HashToken(token)}";

    private static string SessionKey(string userId, string deviceId)
        => $"{SessionPrefix}{userId}:{deviceId}";

    private Task<RefreshToken?> GetRefreshTokenData(string userId, string refreshToken)
        => cache.GetAsync<RefreshToken>(RefreshTokenKey(userId, refreshToken));

}

