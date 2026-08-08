namespace Core.Models.Email;

/// <summary>
/// A claimed verification code remains restorable until the original expiry.
/// The code value is kept only in the in-process operation scope and is never
/// logged.
/// </summary>
public sealed record EmailVerificationClaim(
    string Email,
    EmailCodePurpose Purpose,
    string Code,
    DateTimeOffset ExpiresAt);
