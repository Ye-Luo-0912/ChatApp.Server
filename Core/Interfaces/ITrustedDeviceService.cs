using Core.Models.Auth;

namespace Core.Interfaces;

public interface ITrustedDeviceService
{
    Task<IReadOnlyList<TrustedDeviceDto>> ListAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 签发高熵可信设备令牌（明文仅返回一次）。
    /// 必须通过当前密码（启用 MFA 时另需 TOTP）、一次性 step-up Token，或「最近一次 MFA」标记。
    /// </summary>
    Task<(AuthOperationResult Result, string? PlainToken)> TrustCurrentAsync(
        long userId,
        string? deviceIdHint,
        string? label,
        string? clientIp,
        string? password,
        string? mfaCode,
        string? stepUpToken,
        CancellationToken cancellationToken = default);

    Task<AuthOperationResult> RemoveAsync(long userId, long trustedDeviceId, CancellationToken cancellationToken = default);

    /// <summary>密码变更等场景：撤销该用户全部可信设备。</summary>
    Task<int> RevokeAllAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>确认异常登录并签发可信设备；同样要求 step-up，且返回明文令牌。</summary>
    Task<(AuthOperationResult Result, string? PlainToken)> AcknowledgeUnusualLoginAsync(
        long userId,
        long securityEventId,
        string? deviceIdHint,
        string? clientIp,
        string? password,
        string? mfaCode,
        string? stepUpToken,
        CancellationToken cancellationToken = default);

    /// <summary>校验明文令牌；成功时可轮换并返回新明文（旧令牌立即失效，DB 条件更新）。</summary>
    Task<(bool Ok, string? RotatedPlainToken)> ValidateAndRotateAsync(
        long userId, string plainToken, bool rotate, CancellationToken cancellationToken = default);

    Task<bool> ValidateTokenAsync(long userId, string plainToken, CancellationToken cancellationToken = default);

    /// <summary>校验敏感操作身份（密码 / MFA / step-up / 最近 MFA），不签发可信设备。</summary>
    Task<AuthOperationResult> VerifyStepUpAsync(
        long userId,
        string? password,
        string? mfaCode,
        string? stepUpToken,
        string purpose,
        CancellationToken cancellationToken = default);

    /// <summary>签发短期 step-up Token（绑定 userId+sessionId+deviceHash+purpose+nonce）。</summary>
    Task<(AuthOperationResult Result, string? StepUpToken)> CreateStepUpTokenAsync(
        long userId,
        string? password,
        string? mfaCode,
        string purpose,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// MFA 登录成功后写入「最近一次 MFA」标记（短 TTL，绑定 sessionId+deviceHash，签发可信设备/导出时可消费）。
    /// </summary>
    Task MarkRecentMfaAsync(
        long userId,
        string? sessionId,
        string? deviceId,
        CancellationToken cancellationToken = default);
}

public sealed record TrustedDeviceDto(
    long Id,
    string? DeviceIdHint,
    string? Label,
    string? ClientIp,
    DateTimeOffset TrustedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt);
