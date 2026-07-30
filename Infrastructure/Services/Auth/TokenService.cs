using System.Security.Cryptography;
using System.Text;
using Core.Caching;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Interfaces.Cache;
using Core.Models.Identity;
using Core.Models.Token;
using Core.Settings;
using Infrastructure.Auth;
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
    ICacheValueStore values,
    IAtomicCacheStore atomic,
    ICacheSetStore sets,
    IDeviceInfo deviceInfo,
    IOptions<JwtSettings> options,
    ILogger<TokenService> logger) : ITokenService
{
    // Redis 键前缀
    private const string AccessTokenPrefix  = "AT:";
    private const string RefreshTokenPrefix = "RT:";
    private const string SessionPrefix      = "SS:";
    /// <summary>用户设备索引：SET of deviceId。</summary>
    private const string UserDeviceIndexPrefix = "UDI:";

    private readonly JwtSettings _settings = options.Value;

    /// <summary>
    /// L1 内存缓存：减少认证热路径的 Redis 往返。
    /// TTL = min(5s, 令牌剩余寿命)；负缓存 200ms；撤销时主动驱逐。
    /// </summary>
    private readonly AccessTokenL1Cache? _l1Cache = options.Value.TokenL1CacheEnabled
        ? new AccessTokenL1Cache(
            options.Value.TokenL1CacheMaxEntries,
            options.Value.TokenL1CacheTtlSeconds,
            options.Value.TokenL1CacheNegativeTtlMs)
        : null;

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
    /// 同时填充 L1 缓存，使后续认证请求无需访问 Redis。
    /// </summary>
    public Task StoreAccessTokenAsync(string token, AccessTokenData data, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        var key = AccessTokenKey(token);
        if (_l1Cache is not null && !data.IsExpired)
            _l1Cache.SetPositive(key, data);
        return values.SetAsync(key, data, expiry, cancellationToken);
    }

    /// <summary>
    /// 查询访问令牌对应的元数据，不存在则返回 <see langword="null"/>。
    /// 优先查 L1 内存缓存，未命中再查 Redis 并回填 L1。
    /// </summary>
    public async Task<AccessTokenData?> GetAccessTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        var key = AccessTokenKey(token);

        // L1 检查：正缓存命中 → 返回数据；负缓存命中 → 返回 null
        if (_l1Cache is not null)
        {
            var (found, data) = _l1Cache.TryGet(key);
            if (found)
                return data;
        }

        var redisData = await values.GetAsync<AccessTokenData>(key, cancellationToken).ConfigureAwait(false);

        // 回填 L1
        if (_l1Cache is not null)
        {
            if (redisData is not null && !redisData.IsExpired)
                _l1Cache.SetPositive(key, redisData);
            else if (redisData is null)
                _l1Cache.SetNegative(key);
        }

        return redisData;
    }

    /// <summary>
    /// 从 Redis 中删除访问令牌（主动登出或安全事件触发的强制下线）。
    /// 同时驱逐 L1 缓存中的对应条目。
    /// </summary>
    public Task RevokeAccessTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        var key = AccessTokenKey(token);
        _l1Cache?.Evict(key);
        return values.RemoveAsync(key, cancellationToken);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IRefreshTokenStore
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 将刷新令牌及当前请求的设备信息写入 Redis，同时更新对应的 <see cref="SessionRecord"/>。
    /// </summary>
    public async Task StoreRefreshTokenAsync(string userId, string refreshToken, CancellationToken cancellationToken = default)
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
            values.SetAsync(rtKey, rtData, expiry, cancellationToken),
            values.SetAsync(ssKey, session, expiry, cancellationToken),
            IndexDeviceAsync(userId, device.DeviceId, expiry, cancellationToken));

        logger.LogDebug("刷新令牌及会话已写入缓存，UserId={UserId}, DeviceId={DeviceId}", userId, device.DeviceId);
    }

    /// <summary>
    /// 验证刷新令牌：Redis 中存在 + 未过期 + 设备 ID 与当前请求一致。
    /// </summary>
    public async Task<bool> ValidateRefreshTokenAsync(string userId, string refreshToken, CancellationToken cancellationToken = default)
    {
        var data     = await GetRefreshTokenData(userId, refreshToken, cancellationToken);
        var deviceId = deviceInfo.GetDeviceId();

        if (data is null || !data.IsValid)
            return false;

        // 确认令牌来自同一设备，防止跨设备复用
        return deviceId is not null && data.DeviceId == deviceId;
    }

    /// <summary>
    /// 从 Redis 中删除指定刷新令牌，并同步撤销对应的会话记录（主动登出）。
    /// </summary>
    public async Task RevokeRefreshTokenAsync(string userId, string refreshToken, CancellationToken cancellationToken = default)
    {
        // 先读取设备 ID，以便同步删除会话记录
        var data = await GetRefreshTokenData(userId, refreshToken, cancellationToken);
        await values.RemoveAsync(RefreshTokenKey(userId, refreshToken), cancellationToken);

        if (data?.DeviceId is { } deviceId)
            await RevokeSessionAsync(userId, deviceId, cancellationToken);
    }

    /// <summary>
    /// 查询刷新令牌元数据，不存在则返回 <see langword="null"/>。
    /// </summary>
    public Task<RefreshToken?> GetRefreshTokenAsync(string userId, string refreshToken, CancellationToken cancellationToken = default)
        => GetRefreshTokenData(userId, refreshToken, cancellationToken);

    /// <summary>
    /// 先校验令牌（含设备匹配），通过后立即原子撤销——一次性消费语义。
    /// </summary>
    public async Task<bool> ValidateAndRevokeRefreshTokenAsync(string userId, string refreshToken, CancellationToken cancellationToken = default)
    {
        var deviceId = deviceInfo.GetDeviceId();
        if (deviceId is null)
            return false;

        var consumed = await atomic.TryAtomicConsumeAsync<RefreshToken, bool>(
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
            },
            cancellationToken);

        return consumed.Succeeded;
    }

    /// <summary>
    /// 原子地撤销旧刷新令牌并写入新刷新令牌，继承原始登录时间并递增轮换计数。
    /// <para>同时撤销旧 RT 对应的访问令牌（通过 <see cref="RefreshToken.CurrentAccessTokenKey"/>)。</para>
    /// </summary>
    public async Task<bool> RotateRefreshTokenAsync(string userId, string oldRefreshToken, string newRefreshToken, CancellationToken cancellationToken = default)
    {
        var device = deviceInfo.GenerateDeviceInfo();
        var expiry = TimeSpan.FromDays(_settings.RefreshTokenExpirationDays);
        var rtKey  = RefreshTokenKey(userId, newRefreshToken);
        var ssKey  = SessionKey(userId, device.DeviceId);
        var now    = DateTime.UtcNow;

        var rotated = await atomic.TryAtomicConsumeAsync<RefreshToken, bool>(
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
                        new CacheSetRequest { Key = rtKey, Value = newRtData, Expiration = expiry },
                        new CacheSetRequest { Key = ssKey, Value = newSession, Expiration = expiry },
                    ],
                };
            },
            cancellationToken);

        if (rotated.Succeeded)
        {
            await IndexDeviceAsync(userId, device.DeviceId, expiry, cancellationToken);
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
    public async Task<TokenIssueResult> IssueLoginTokensAsync(ApplicationUser user, IList<string> roles, CancellationToken cancellationToken = default)
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

        await atomic.SetManyAsync(
            [
                new CacheSetRequest
                {
                    Key = atKey,
                    Value = atData,
                    Expiration = accessExpiry,
                },
                new CacheSetRequest { Key = rtKey, Value = rtData, Expiration = refreshExpiry },
                new CacheSetRequest { Key = ssKey, Value = session, Expiration = refreshExpiry },
            ],
            cancellationToken);

        await IndexDeviceAsync(userId, device.DeviceId, refreshExpiry, cancellationToken);

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
    public async Task<string> IssueAccessTokenAsync(ApplicationUser user, IList<string> roles, string? sessionId = null, CancellationToken cancellationToken = default)
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

        await StoreAccessTokenAsync(token, data, expiry, cancellationToken);
        return token;
    }

    /// <summary>
    /// 原子地完成令牌轮换：CAS 消费旧 RT，并在同一事务中写入新 AT / RT / Session。
    /// </summary>
    public async Task<(string accessToken, string refreshToken)?> IssueRefreshTokensAsync(
        string userId, string oldRefreshToken, ApplicationUser user, IList<string> roles, CancellationToken cancellationToken = default)
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

        var rotated = await atomic.TryAtomicConsumeAsync<RefreshToken, (string accessToken, string refreshToken)>(
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
                        new CacheSetRequest
                        {
                            Key = atKey,
                            Value = atData,
                            Expiration = accessExpiry,
                        },
                        new CacheSetRequest { Key = rtKey, Value = newRtData, Expiration = refreshExpiry },
                        new CacheSetRequest { Key = ssKey, Value = session, Expiration = refreshExpiry },
                    ],
                };
            },
            cancellationToken);

        if (!rotated.Succeeded)
        {
            logger.LogDebug("令牌轮换失败（无效或已被并发消费），UserId={UserId}", userId);
            return null;
        }

        await IndexDeviceAsync(userId, device.DeviceId, refreshExpiry, cancellationToken);

        logger.LogDebug("令牌已轮换，UserId={UserId}, DeviceId={DeviceId}", userId, device.DeviceId);
        return rotated.Value;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ISessionStore
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 查询指定用户在指定设备上的会话记录；不存在则返回 <see langword="null"/>。
    /// </summary>
    public Task<SessionRecord?> GetSessionAsync(string userId, string deviceId, CancellationToken cancellationToken = default)
        => values.GetAsync<SessionRecord>(SessionKey(userId, deviceId), cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionRecord>> ListSessionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var deviceIds = await sets.SetMembersAsync(UserDeviceIndexKey(userId), cancellationToken).ConfigureAwait(false);
        if (deviceIds.Count == 0)
            return [];

        var keys = new string[deviceIds.Count];
        for (var i = 0; i < deviceIds.Count; i++)
            keys[i] = SessionKey(userId, deviceIds[i]);

        var cachedSessions = await values.GetManyAsync<SessionRecord>(keys, cancellationToken)
            .ConfigureAwait(false);
        var sessions = new List<SessionRecord>(deviceIds.Count);
        var stale = new List<string>();
        for (var i = 0; i < deviceIds.Count; i++)
        {
            var deviceId = deviceIds[i];
            var session = cachedSessions[i];
            if (session is null || !session.IsActive)
            {
                stale.Add(deviceId);
                continue;
            }

            sessions.Add(session);
        }

        if (stale.Count > 0)
        {
            await Task.WhenAll(stale.Select(id =>
                sets.SetRemoveAsync(UserDeviceIndexKey(userId), id, cancellationToken))).ConfigureAwait(false);
        }

        return sessions;
    }

    /// <summary>
    /// 撤销（删除）指定用户在指定设备上的会话记录，并同步删除对应的访问令牌和刷新令牌。
    /// </summary>
    public async Task RevokeSessionAsync(string userId, string deviceId, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(userId, deviceId, cancellationToken);
        var tasks = new List<Task>(4)
        {
            values.RemoveAsync(SessionKey(userId, deviceId), cancellationToken),
            sets.SetRemoveAsync(UserDeviceIndexKey(userId), deviceId, cancellationToken),
        };

        if (session?.CurrentAccessTokenKey  is not null)
        {
            _l1Cache?.Evict(session.CurrentAccessTokenKey);
            tasks.Add(values.RemoveAsync(session.CurrentAccessTokenKey, cancellationToken));
        }
        if (session?.CurrentRefreshTokenKey is not null) tasks.Add(values.RemoveAsync(session.CurrentRefreshTokenKey, cancellationToken));

        await Task.WhenAll(tasks);
        logger.LogDebug("会话已撤销，UserId={UserId}, DeviceId={DeviceId}", userId, deviceId);
    }

    /// <inheritdoc />
    public async Task<int> RevokeAllSessionsAsync(string userId, string? exceptDeviceId = null, CancellationToken cancellationToken = default)
    {
        var deviceIds = await sets.SetMembersAsync(UserDeviceIndexKey(userId), cancellationToken).ConfigureAwait(false);
        var revoked = 0;

        foreach (var deviceId in deviceIds)
        {
            if (exceptDeviceId is not null && string.Equals(deviceId, exceptDeviceId, StringComparison.Ordinal))
                continue;

            await RevokeSessionAsync(userId, deviceId, cancellationToken).ConfigureAwait(false);
            revoked++;
        }

        if (exceptDeviceId is null)
            await values.RemoveAsync(UserDeviceIndexKey(userId), cancellationToken).ConfigureAwait(false);

        return revoked;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 私有辅助方法
    // ─────────────────────────────────────────────────────────────────────────

    private Task IndexDeviceAsync(string userId, string deviceId, TimeSpan ttl, CancellationToken cancellationToken)
        => sets.SetAddAsync(UserDeviceIndexKey(userId), deviceId, ttl, cancellationToken);

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

    private static string UserDeviceIndexKey(string userId)
        => $"{UserDeviceIndexPrefix}{userId}";

    private Task<RefreshToken?> GetRefreshTokenData(string userId, string refreshToken, CancellationToken cancellationToken = default)
        => values.GetAsync<RefreshToken>(RefreshTokenKey(userId, refreshToken), cancellationToken);

}

