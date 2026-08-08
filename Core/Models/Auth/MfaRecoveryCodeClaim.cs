namespace Core.Models.Auth;

/// <summary>
/// Durable claim for a recovery code. The code digest is removed from the
/// user's active list while this record is <see cref="MfaRecoveryCodeClaimState.Claimed" />.
/// A caller must complete or restore the claim; an expired claim can be
/// reconciled without extending the original credential lifetime.
/// </summary>
public sealed record MfaRecoveryCodeClaim(
    long Id,
    long UserId,
    string ClaimToken,
    DateTimeOffset ExpiresAt);

public enum MfaRecoveryCodeClaimState : short
{
    Claimed = 0,
    Completed = 1,
    Restored = 2,
    Expired = 3,
}
