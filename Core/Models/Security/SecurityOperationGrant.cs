namespace Core.Models.Security;

/// <summary>
/// Durable one-time authorization for a sensitive security operation.
/// The presented grant token is never persisted; only its SHA-256 digest is.
/// </summary>
public enum SecurityOperationGrantState : byte
{
    Available = 0,
    Claimed = 1,
    Completed = 2,
    Restored = 3,
    Expired = 4,
}

public sealed class SecurityOperationGrant
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string GrantHash { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string? PayloadHash { get; set; }
    public SecurityOperationGrantState State { get; set; } = SecurityOperationGrantState.Available;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
