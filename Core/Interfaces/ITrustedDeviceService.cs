using Core.Models.Auth;

namespace Core.Interfaces;

public interface ITrustedDeviceService
{
    Task<IReadOnlyList<TrustedDeviceDto>> ListAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>签发高熵可信设备令牌；明文仅返回一次。</summary>
    Task<(AuthOperationResult Result, string? PlainToken)> TrustCurrentAsync(
        long userId, string? deviceIdHint, string? label, string? clientIp,
        CancellationToken cancellationToken = default);

    Task<AuthOperationResult> RemoveAsync(long userId, long trustedDeviceId, CancellationToken cancellationToken = default);

    Task<AuthOperationResult> AcknowledgeUnusualLoginAsync(
        long userId, long securityEventId, string? deviceIdHint, string? clientIp,
        CancellationToken cancellationToken = default);

    Task<bool> ValidateTokenAsync(long userId, string plainToken, CancellationToken cancellationToken = default);
}

public sealed record TrustedDeviceDto(
    long Id,
    string? DeviceIdHint,
    string? Label,
    string? ClientIp,
    DateTimeOffset TrustedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt);
