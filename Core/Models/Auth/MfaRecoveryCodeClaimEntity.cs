namespace Core.Models.Auth;

/// <summary>
/// Persistence model for <see cref="MfaRecoveryCodeClaim"/>.
/// </summary>
public sealed class MfaRecoveryCodeClaimEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string ClaimToken { get; set; } = string.Empty;
    public string CodeDigest { get; set; } = string.Empty;
    public string OriginalCodesJson { get; set; } = string.Empty;
    public string RemainingCodesJson { get; set; } = string.Empty;
    public MfaRecoveryCodeClaimState State { get; set; }
    public DateTimeOffset ClaimedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

}
