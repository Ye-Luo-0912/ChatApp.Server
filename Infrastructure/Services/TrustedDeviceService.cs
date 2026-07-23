using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using Core.Caching;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Models.Auth;
using Core.Models.Security;
using Core.Models.Token;
using Core.Settings;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public sealed class TrustedDeviceService(
    UserDbContext db,
    ISecurityEventStore securityEventStore,
    IPasswordHasher passwordHasher,
    IMfaService mfaService,
    ICacheProvider cache,
    IDeviceInfo deviceInfo,
    IHttpContextAccessor httpContextAccessor,
    IOptions<TrustedDeviceOptions> trustedDeviceOptions,
    ILogger<TrustedDeviceService> logger) : ITrustedDeviceService
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(90);
    public static readonly TimeSpan StepUpTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RecentMfaTtl = TimeSpan.FromMinutes(10);

    private readonly int _maxDevices = Math.Max(1, trustedDeviceOptions.Value.MaxDevicesPerUser);
    private readonly TimeSpan _lastSeenThrottle = TimeSpan.FromHours(
        Math.Clamp(trustedDeviceOptions.Value.LastSeenThrottleHours, 0.05, 168));

    public async Task<IReadOnlyList<TrustedDeviceDto>> ListAsync(
        long userId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.TrustedDevices.AsNoTracking()
            .Where(d => d.UserId == userId && d.RevokedAt == null && d.ExpiresAt > now)
            .OrderByDescending(d => d.LastSeenAt)
            .Select(d => new TrustedDeviceDto(
                d.Id, d.DeviceIdHint, d.Label, d.ClientIp, d.TrustedAt, d.LastSeenAt, d.ExpiresAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<(AuthOperationResult Result, string? PlainToken)> TrustCurrentAsync(
        long userId,
        string? deviceIdHint,
        string? label,
        string? clientIp,
        string? password,
        string? mfaCode,
        string? stepUpToken,
        CancellationToken cancellationToken = default)
    {
        var stepUp = await EnsureStepUpAsync(
                userId, password, mfaCode, stepUpToken, StepUpPurposes.TrustedDevice, cancellationToken)
            .ConfigureAwait(false);
        if (!stepUp.Succeeded)
            return (stepUp, null);

        return await IssueTrustedDeviceAsync(
            userId, deviceIdHint, label, clientIp, cancellationToken).ConfigureAwait(false);
    }

    public Task<AuthOperationResult> VerifyStepUpAsync(
        long userId, string? password, string? mfaCode, string? stepUpToken, string purpose,
        CancellationToken cancellationToken = default)
        => EnsureStepUpAsync(userId, password, mfaCode, stepUpToken, purpose, cancellationToken);

    public async Task<AuthOperationResult> RemoveAsync(
        long userId, long trustedDeviceId, CancellationToken cancellationToken = default)
    {
        var device = await db.TrustedDevices
            .FirstOrDefaultAsync(d => d.Id == trustedDeviceId && d.UserId == userId, cancellationToken);
        if (device is null)
            return AuthOperationResult.Fail("NotFound", "可信设备不存在");

        device.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await securityEventStore.RecordAsync(
            userId, SecurityEventType.TrustedDeviceRemoved, device.DeviceIdHint,
            detail: $"id={trustedDeviceId}", cancellationToken: cancellationToken);
        AuthSecurityMetrics.RecordTrusted("removed");
        return AuthOperationResult.Success();
    }

    public async Task<int> RevokeAllAsync(long userId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = await db.TrustedDevices
            .Where(d => d.UserId == userId && d.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.RevokedAt, now), cancellationToken);
        if (updated > 0)
        {
            await securityEventStore.RecordAsync(
                userId, SecurityEventType.TrustedDeviceRemoved,
                detail: $"revokedAll={updated}", cancellationToken: cancellationToken);
            AuthSecurityMetrics.RecordTrusted("revoked_all");
            logger.LogInformation("用户 {UserId} 已撤销全部可信设备 count={Count}", userId, updated);
        }

        return updated;
    }

    public async Task<(AuthOperationResult Result, string? PlainToken)> AcknowledgeUnusualLoginAsync(
        long userId,
        long securityEventId,
        string? deviceIdHint,
        string? clientIp,
        string? password,
        string? mfaCode,
        string? stepUpToken,
        CancellationToken cancellationToken = default)
    {
        var evt = await db.SecurityEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == securityEventId && e.UserId == userId, cancellationToken);
        if (evt is null)
            return (AuthOperationResult.Fail("NotFound", "安全事件不存在"), null);
        if (evt.EventType is not (SecurityEventType.LoginUnusualLocation or SecurityEventType.LoginNewDevice))
            return (AuthOperationResult.Fail("InvalidEvent", "仅可确认新设备或异常地点登录事件"), null);

        var stepUp = await EnsureStepUpAsync(
                userId, password, mfaCode, stepUpToken, StepUpPurposes.TrustedDevice, cancellationToken)
            .ConfigureAwait(false);
        if (!stepUp.Succeeded)
            return (stepUp, null);

        var (issued, plain) = await IssueTrustedDeviceAsync(
            userId,
            deviceIdHint ?? evt.DeviceId,
            "经本人确认",
            clientIp ?? evt.ClientIp,
            cancellationToken).ConfigureAwait(false);
        if (!issued.Succeeded)
            return (issued, null);

        await securityEventStore.RecordAsync(
            userId, SecurityEventType.UnusualLoginAcknowledged,
            deviceIdHint ?? evt.DeviceId, clientIp ?? evt.ClientIp,
            detail: $"sourceEvent={securityEventId}", cancellationToken: cancellationToken);
        return (AuthOperationResult.Success(), plain);
    }

    public async Task<(bool Ok, string? RotatedPlainToken)> ValidateAndRotateAsync(
        long userId, string plainToken, bool rotate, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(plainToken))
            return (false, null);

        var oldHash = HashToken(plainToken);
        var now = DateTimeOffset.UtcNow;
        var lastSeenBefore = now - _lastSeenThrottle;

        if (!rotate)
        {
            // WHERE 带时间条件：未过节流窗口则 0 行改写，避免写放大。
            var touched = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "T_TrustedDevice"
                SET "LastSeenAt" = {now}
                WHERE "UserId" = {userId}
                  AND "TokenHash" = {oldHash}
                  AND "RevokedAt" IS NULL
                  AND "ExpiresAt" > {now}
                  AND "LastSeenAt" < {lastSeenBefore}
                """, cancellationToken).ConfigureAwait(false);
            if (touched > 0)
            {
                AuthSecurityMetrics.RecordTrusted("validate");
                return (true, null);
            }

            // 令牌有效但 LastSeen 仍新鲜（或令牌无效）— 廉价存在性检查，不改写行。
            var exists = await db.TrustedDevices.AsNoTracking()
                .AnyAsync(
                    d => d.UserId == userId
                         && d.TokenHash == oldHash
                         && d.RevokedAt == null
                         && d.ExpiresAt > now,
                    cancellationToken)
                .ConfigureAwait(false);
            if (exists)
                AuthSecurityMetrics.RecordTrusted("validate");
            return (exists, null);
        }

        var rotated = CreatePlainToken();
        var newHash = HashToken(rotated);
        var expires = now.Add(DefaultLifetime);

        // 原子 CAS：旧哈希只能被一个并发请求成功消费；轮转本身必改写行，顺带刷新 LastSeen。
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "T_TrustedDevice"
            SET "TokenHash" = {newHash},
                "ExpiresAt" = {expires},
                "LastSeenAt" = {now}
            WHERE "UserId" = {userId}
              AND "TokenHash" = {oldHash}
              AND "RevokedAt" IS NULL
              AND "ExpiresAt" > {now}
            """, cancellationToken).ConfigureAwait(false);

        if (updated <= 0)
            return (false, null);

        AuthSecurityMetrics.RecordTrusted("validate_rotate");
        return (true, rotated);
    }

    public async Task<bool> ValidateTokenAsync(
        long userId, string plainToken, CancellationToken cancellationToken = default)
    {
        var (ok, _) = await ValidateAndRotateAsync(userId, plainToken, rotate: false, cancellationToken);
        return ok;
    }

    public async Task<(AuthOperationResult Result, string? StepUpToken)> CreateStepUpTokenAsync(
        long userId, string? password, string? mfaCode, string purpose,
        CancellationToken cancellationToken = default)
    {
        if (!StepUpPurposes.IsKnown(purpose))
            return (AuthOperationResult.Fail("InvalidPurpose", "未知的 step-up 用途"), null);

        var verified = await VerifyPasswordAndMfaAsync(userId, password, mfaCode, cancellationToken)
            .ConfigureAwait(false);
        if (!verified.Succeeded)
            return (verified, null);

        var ctx = ResolveCurrentBinding(purpose);
        if (ctx is null)
            return (AuthOperationResult.Fail("MissingSession", "缺少会话或设备绑定上下文"), null);

        var plain = CreatePlainToken();
        var key = CacheKeyBuilder.WithPrefix(CacheConstants.StepUpPrefix, HashToken(plain));
        var payload = FormatStepUpPayload(userId, ctx.Value);
        await cache.StringSetAsync(key, payload, StepUpTtl, cancellationToken)
            .ConfigureAwait(false);
        AuthSecurityMetrics.RecordTrusted("step_up_issued");
        return (AuthOperationResult.Success(), plain);
    }

    public Task MarkRecentMfaAsync(
        long userId, string? sessionId, string? deviceId, CancellationToken cancellationToken = default)
    {
        var deviceHash = FormatDeviceHash(deviceId);
        var sid = string.IsNullOrWhiteSpace(sessionId) ? "none" : sessionId.Trim();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var key = RecentMfaKey(userId, sid, deviceHash);
        // value = nonce（一次性）；绑定已体现在 key 中
        return cache.StringSetAsync(key, nonce, RecentMfaTtl, cancellationToken);
    }

    private async Task<(AuthOperationResult Result, string? PlainToken)> IssueTrustedDeviceAsync(
        long userId, string? deviceIdHint, string? label, string? clientIp,
        CancellationToken cancellationToken)
    {
        // 用户行锁：并发签发时串行化 Count→Insert，避免超过 MaxDevicesPerUser
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {userId} FOR UPDATE""",
                cancellationToken)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var activeCount = await db.TrustedDevices
            .CountAsync(
                d => d.UserId == userId && d.RevokedAt == null && d.ExpiresAt > now,
                cancellationToken)
            .ConfigureAwait(false);
        if (activeCount >= _maxDevices)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return (AuthOperationResult.Fail(
                "TrustedDeviceLimit",
                $"最多可信任 {_maxDevices} 台设备，请先移除旧设备"), null);
        }

        var plainToken = CreatePlainToken();
        var hash = HashToken(plainToken);

        db.TrustedDevices.Add(new TrustedDevice
        {
            UserId = userId,
            DeviceIdHint = string.IsNullOrWhiteSpace(deviceIdHint) ? null : deviceIdHint.Trim(),
            TokenHash = hash,
            Label = label,
            ClientIp = clientIp,
            TrustedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.Add(DefaultLifetime),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        await securityEventStore.RecordAsync(
            userId, SecurityEventType.TrustedDeviceAdded, deviceIdHint, clientIp,
            detail: label, cancellationToken: cancellationToken).ConfigureAwait(false);
        AuthSecurityMetrics.RecordTrusted("issued");
        logger.LogInformation("用户 {UserId} 签发可信设备令牌", userId);
        return (AuthOperationResult.Success(), plainToken);
    }

    private async Task<AuthOperationResult> EnsureStepUpAsync(
        long userId, string? password, string? mfaCode, string? stepUpToken, string purpose,
        CancellationToken cancellationToken)
    {
        if (!StepUpPurposes.IsKnown(purpose))
            return AuthOperationResult.Fail("InvalidPurpose", "未知的 step-up 用途");

        var current = ResolveCurrentBinding(purpose);
        if (current is null)
            return AuthOperationResult.Fail("MissingSession", "缺少会话或设备绑定上下文");

        if (!string.IsNullOrWhiteSpace(stepUpToken))
        {
            var key = CacheKeyBuilder.WithPrefix(CacheConstants.StepUpPrefix, HashToken(stepUpToken.Trim()));
            var stored = await cache.StringGetAsync(key, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (TryParseStepUpPayload(stored, out var boundUserId, out var bound)
                && boundUserId == userId
                && BindingsMatch(bound, current.Value)
                && await cache.TryStringCompareAndDeleteAsync(key, stored!, cancellationToken).ConfigureAwait(false))
            {
                AuthSecurityMetrics.RecordTrusted("step_up_consumed");
                return AuthOperationResult.Success();
            }

            return AuthOperationResult.Fail("InvalidStepUp", "step-up 令牌无效、已过期或绑定不匹配");
        }

        // 最近一次 MFA（登录校验成功后写入），一次性消费；绑定 session+device，用途不限（MFA 已覆盖）。
        var recentKey = RecentMfaKey(userId, current.Value.SessionId, current.Value.DeviceHash);
        var recent = await cache.StringGetAsync(recentKey, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrEmpty(recent)
            && await cache.TryStringCompareAndDeleteAsync(recentKey, recent, cancellationToken)
                .ConfigureAwait(false))
        {
            AuthSecurityMetrics.RecordTrusted("recent_mfa_consumed");
            return AuthOperationResult.Success();
        }

        return await VerifyPasswordAndMfaAsync(userId, password, mfaCode, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AuthOperationResult> VerifyPasswordAndMfaAsync(
        long userId, string? password, string? mfaCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(password))
            return AuthOperationResult.Fail(
                "StepUpRequired",
                "签发可信设备需提供当前密码、MFA 验证码或有效的 step-up 令牌");

        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
            return AuthOperationResult.Fail("NotFound", "用户不存在");
        if (string.IsNullOrWhiteSpace(user.PasswordHash)
            || !await passwordHasher.VerifyPasswordAsync(password, user.PasswordHash, cancellationToken)
                .ConfigureAwait(false))
            return AuthOperationResult.Fail("InvalidPassword", "密码验证失败");

        if (user.TwoFactorEnabled && !string.IsNullOrWhiteSpace(user.TotpSecret))
        {
            if (string.IsNullOrWhiteSpace(mfaCode)
                || !await mfaService.TryVerifyAndConsumeTotpForUserAsync(user, mfaCode, cancellationToken)
                    .ConfigureAwait(false))
                return AuthOperationResult.Fail("MfaRequired", "已启用 MFA，请提供当前验证码");
        }

        return AuthOperationResult.Success();
    }

    private (string SessionId, string DeviceHash, string Purpose, string Nonce)? ResolveCurrentBinding(string purpose)
    {
        var deviceId = deviceInfo.GetDeviceId();
        var deviceHash = FormatDeviceHash(deviceId);
        var http = httpContextAccessor.HttpContext;
        var sessionId = http?.User?.FindFirstValue(AuthClaimTypes.SessionId);

        // 已认证请求必须带 sid 声明，防止跨会话复用 step-up。
        if (http?.User?.Identity?.IsAuthenticated == true && string.IsNullOrWhiteSpace(sessionId))
            return null;

        if (string.IsNullOrWhiteSpace(sessionId))
            sessionId = "none";

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return (sessionId.Trim(), deviceHash, purpose, nonce);
    }

    private static bool BindingsMatch(
        (string SessionId, string DeviceHash, string Purpose, string Nonce) stored,
        (string SessionId, string DeviceHash, string Purpose, string Nonce) current)
        => string.Equals(stored.SessionId, current.SessionId, StringComparison.Ordinal)
           && string.Equals(stored.DeviceHash, current.DeviceHash, StringComparison.Ordinal)
           && string.Equals(stored.Purpose, current.Purpose, StringComparison.Ordinal);

    private static string FormatStepUpPayload(
        long userId, (string SessionId, string DeviceHash, string Purpose, string Nonce) ctx)
        => $"v2|{userId}|{ctx.SessionId}|{ctx.DeviceHash}|{ctx.Purpose}|{ctx.Nonce}";

    private static bool TryParseStepUpPayload(
        string? payload,
        out long userId,
        out (string SessionId, string DeviceHash, string Purpose, string Nonce) binding)
    {
        userId = 0;
        binding = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        // 旧格式：仅 userId —— 拒绝（强制升级绑定）。
        if (!payload.StartsWith("v2|", StringComparison.Ordinal))
            return false;

        var parts = payload.Split('|', 6);
        if (parts.Length != 6)
            return false;
        if (!long.TryParse(parts[1], out userId))
            return false;
        binding = (parts[2], parts[3], parts[4], parts[5]);
        return !string.IsNullOrWhiteSpace(binding.SessionId)
               && !string.IsNullOrWhiteSpace(binding.DeviceHash)
               && !string.IsNullOrWhiteSpace(binding.Purpose)
               && !string.IsNullOrWhiteSpace(binding.Nonce);
    }

    private static string RecentMfaKey(long userId, string sessionId, string deviceHash)
        => CacheKeyBuilder.WithPrefix(
            CacheConstants.RecentMfaPrefix,
            $"{userId}:{sessionId}:{deviceHash}");

    private static string FormatDeviceHash(string? deviceId)
    {
        var hash = DeviceIdHashHelper.Compute(deviceId);
        return hash is { } h ? h.ToString("x16") : "none";
    }

    private static string CreatePlainToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HashToken(string plainToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainToken));
        return Convert.ToHexString(bytes);
    }
}
