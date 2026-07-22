using System.Security.Cryptography;
using System.Text;
using Core.Caching;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Models.Auth;
using Core.Models.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class TrustedDeviceService(
    UserDbContext db,
    ISecurityEventStore securityEventStore,
    IPasswordHasher passwordHasher,
    IMfaService mfaService,
    ICacheProvider cache,
    ILogger<TrustedDeviceService> logger) : ITrustedDeviceService
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(90);
    public static readonly TimeSpan StepUpTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RecentMfaTtl = TimeSpan.FromMinutes(10);

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
        var stepUp = await EnsureStepUpAsync(userId, password, mfaCode, stepUpToken, cancellationToken)
            .ConfigureAwait(false);
        if (!stepUp.Succeeded)
            return (stepUp, null);

        return await IssueTrustedDeviceAsync(
            userId, deviceIdHint, label, clientIp, cancellationToken).ConfigureAwait(false);
    }

    public Task<AuthOperationResult> VerifyStepUpAsync(
        long userId, string? password, string? mfaCode, string? stepUpToken,
        CancellationToken cancellationToken = default)
        => EnsureStepUpAsync(userId, password, mfaCode, stepUpToken, cancellationToken);

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

        var stepUp = await EnsureStepUpAsync(userId, password, mfaCode, stepUpToken, cancellationToken)
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
        var hourFloor = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);

        if (!rotate)
        {
            // 仅校验并按小时合并 LastSeenAt（条件更新，避免无意义写放大）
            var touched = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "T_TrustedDevice"
                SET "LastSeenAt" = CASE
                    WHEN "LastSeenAt" < {hourFloor} THEN {now}
                    ELSE "LastSeenAt"
                END
                WHERE "UserId" = {userId}
                  AND "TokenHash" = {oldHash}
                  AND "RevokedAt" IS NULL
                  AND "ExpiresAt" > {now}
                """, cancellationToken).ConfigureAwait(false);
            if (touched > 0)
                AuthSecurityMetrics.RecordTrusted("validate");
            return (touched > 0, null);
        }

        var rotated = CreatePlainToken();
        var newHash = HashToken(rotated);
        var expires = now.Add(DefaultLifetime);

        // 原子 CAS：旧哈希只能被一个并发请求成功消费
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "T_TrustedDevice"
            SET "TokenHash" = {newHash},
                "ExpiresAt" = {expires},
                "LastSeenAt" = CASE
                    WHEN "LastSeenAt" < {hourFloor} THEN {now}
                    ELSE "LastSeenAt"
                END
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
        long userId, string? password, string? mfaCode, CancellationToken cancellationToken = default)
    {
        var verified = await VerifyPasswordAndMfaAsync(userId, password, mfaCode, cancellationToken)
            .ConfigureAwait(false);
        if (!verified.Succeeded)
            return (verified, null);

        var plain = CreatePlainToken();
        var key = CacheKeyBuilder.WithPrefix(CacheConstants.StepUpPrefix, HashToken(plain));
        await cache.StringSetAsync(key, userId.ToString(), StepUpTtl, cancellationToken)
            .ConfigureAwait(false);
        AuthSecurityMetrics.RecordTrusted("step_up_issued");
        return (AuthOperationResult.Success(), plain);
    }

    public Task MarkRecentMfaAsync(long userId, CancellationToken cancellationToken = default)
    {
        var key = CacheKeyBuilder.WithPrefix(CacheConstants.RecentMfaPrefix, userId.ToString());
        return cache.StringSetAsync(key, "1", RecentMfaTtl, cancellationToken);
    }

    private async Task<(AuthOperationResult Result, string? PlainToken)> IssueTrustedDeviceAsync(
        long userId, string? deviceIdHint, string? label, string? clientIp,
        CancellationToken cancellationToken)
    {
        var plainToken = CreatePlainToken();
        var hash = HashToken(plainToken);
        var now = DateTimeOffset.UtcNow;

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
        await db.SaveChangesAsync(cancellationToken);
        await securityEventStore.RecordAsync(
            userId, SecurityEventType.TrustedDeviceAdded, deviceIdHint, clientIp,
            detail: label, cancellationToken: cancellationToken);
        AuthSecurityMetrics.RecordTrusted("issued");
        logger.LogInformation("用户 {UserId} 签发可信设备令牌", userId);
        return (AuthOperationResult.Success(), plainToken);
    }

    private async Task<AuthOperationResult> EnsureStepUpAsync(
        long userId, string? password, string? mfaCode, string? stepUpToken,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(stepUpToken))
        {
            var key = CacheKeyBuilder.WithPrefix(CacheConstants.StepUpPrefix, HashToken(stepUpToken.Trim()));
            var owner = await cache.StringGetAsync(key, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(owner, userId.ToString(), StringComparison.Ordinal)
                && await cache.TryStringCompareAndDeleteAsync(key, owner!, cancellationToken).ConfigureAwait(false))
            {
                AuthSecurityMetrics.RecordTrusted("step_up_consumed");
                return AuthOperationResult.Success();
            }

            return AuthOperationResult.Fail("InvalidStepUp", "step-up 令牌无效或已过期");
        }

        // 最近一次 MFA（登录校验成功后写入），一次性消费
        var recentKey = CacheKeyBuilder.WithPrefix(CacheConstants.RecentMfaPrefix, userId.ToString());
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
            if (string.IsNullOrWhiteSpace(mfaCode) || !mfaService.VerifyTotpForUser(user, mfaCode))
                return AuthOperationResult.Fail("MfaRequired", "已启用 MFA，请提供当前验证码");
        }

        return AuthOperationResult.Success();
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
