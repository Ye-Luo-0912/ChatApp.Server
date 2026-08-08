using Core.Models.Security;

namespace Core.Interfaces.Auth;

/// <summary>
/// Durable Available → Claimed → Completed/Restored security-operation grant.
/// A process crash leaves a claim reclaimable only through its explicit expiry
/// policy; completing or restoring always fences by row id and claim token.
/// </summary>
public interface ISecurityOperationGrantStore
{
    Task<string> IssueAsync(
        long userId,
        string purpose,
        TimeSpan lifetime,
        string? payloadHash = null,
        CancellationToken cancellationToken = default);

    Task<SecurityOperationGrant?> ClaimAsync(
        long userId,
        string grantToken,
        string purpose,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims a grant when the user id is carried by the durable grant itself.
    /// This is used for anonymous continuation steps such as MFA completion;
    /// the presented token is still compared by its hash and purpose.
    /// </summary>
    Task<SecurityOperationGrant?> ClaimAsync(
        string grantToken,
        string purpose,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteAsync(
        SecurityOperationGrant grant,
        CancellationToken cancellationToken = default);

    Task<bool> RestoreAsync(
        SecurityOperationGrant grant,
        CancellationToken cancellationToken = default);
}
