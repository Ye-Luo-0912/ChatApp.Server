using System.Security.Cryptography;
using System.Text;
using Core.Interfaces;
using Core.Models.Auth;
using Core.Models.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class TrustedDeviceService(
    UserDbContext db,
    ISecurityEventStore securityEventStore,
    ILogger<TrustedDeviceService> logger) : ITrustedDeviceService
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(90);

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
        long userId, string? deviceIdHint, string? label, string? clientIp,
        CancellationToken cancellationToken = default)
    {
        var plainToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
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
        logger.LogInformation("用户 {UserId} 签发可信设备令牌", userId);
        return (AuthOperationResult.Success(), plainToken);
    }

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
        return AuthOperationResult.Success();
    }

    public async Task<AuthOperationResult> AcknowledgeUnusualLoginAsync(
        long userId, long securityEventId, string? deviceIdHint, string? clientIp,
        CancellationToken cancellationToken = default)
    {
        var evt = await db.SecurityEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == securityEventId && e.UserId == userId, cancellationToken);
        if (evt is null)
            return AuthOperationResult.Fail("NotFound", "安全事件不存在");
        if (evt.EventType is not (SecurityEventType.LoginUnusualLocation or SecurityEventType.LoginNewDevice))
            return AuthOperationResult.Fail("InvalidEvent", "仅可确认新设备或异常地点登录事件");

        await TrustCurrentAsync(userId, deviceIdHint ?? evt.DeviceId, "经本人确认", clientIp ?? evt.ClientIp,
            cancellationToken);

        await securityEventStore.RecordAsync(
            userId, SecurityEventType.UnusualLoginAcknowledged,
            deviceIdHint ?? evt.DeviceId, clientIp ?? evt.ClientIp,
            detail: $"sourceEvent={securityEventId}", cancellationToken: cancellationToken);
        return AuthOperationResult.Success();
    }

    public async Task<bool> ValidateTokenAsync(
        long userId, string plainToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(plainToken))
            return false;
        var hash = HashToken(plainToken);
        var now = DateTimeOffset.UtcNow;
        var device = await db.TrustedDevices
            .FirstOrDefaultAsync(
                d => d.UserId == userId && d.TokenHash == hash && d.RevokedAt == null && d.ExpiresAt > now,
                cancellationToken);
        if (device is null)
            return false;

        device.LastSeenAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string HashToken(string plainToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainToken));
        return Convert.ToHexString(bytes);
    }
}
