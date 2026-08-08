namespace Core.Models;

/// <summary>手机号验证码 Claim；ExpiresAt 保留原始截止时间，恢复不得刷新 TTL。</summary>
public sealed record PhoneVerificationClaim(
    string PhoneNumber,
    string Code,
    DateTimeOffset ExpiresAt);
