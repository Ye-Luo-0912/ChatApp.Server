namespace Core.Models.Auth;

/// <summary>
/// A TOTP time-step claim. The Redis marker is retained until its natural
/// replay-prevention expiry after a successful mutation, and can be restored
/// only while the owning mutation has not committed.
/// </summary>
public sealed record MfaVerificationClaim(
    string Key,
    string Marker,
    DateTimeOffset ExpiresAt);
