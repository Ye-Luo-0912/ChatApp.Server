using System.Security.Cryptography;
using System.Text;
using Core.Interfaces.Auth;
using Core.Models.Security;
using Core.Models.Token;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services.Auth;

/// <summary>PostgreSQL-backed one-time security-operation grant store.</summary>
public sealed class SecurityOperationGrantStore(UserDbContext db) : ISecurityOperationGrantStore
{
    private const int MaxPurposeLength = 64;

    public async Task<string> IssueAsync(
        long userId,
        string purpose,
        TimeSpan lifetime,
        string? payloadHash = null,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
            throw new ArgumentOutOfRangeException(nameof(userId));
        if (string.IsNullOrWhiteSpace(purpose))
            throw new ArgumentException("grant purpose is required", nameof(purpose));

        var normalizedPurpose = purpose.Trim();
        if (normalizedPurpose.Length > MaxPurposeLength)
            throw new ArgumentException("grant purpose is too long", nameof(purpose));

        var lifetimeValue = lifetime <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(5)
            : lifetime;
        var token = TokenBufferEncoding.CreateBase64Url(32);
        var now = DateTimeOffset.UtcNow;
        db.SecurityOperationGrants.Add(new SecurityOperationGrant
        {
            UserId = userId,
            GrantHash = ComputeHash(token),
            Purpose = normalizedPurpose,
            PayloadHash = NormalizePayloadHash(payloadHash),
            State = SecurityOperationGrantState.Available,
            CreatedAt = now,
            ExpiresAt = now.Add(lifetimeValue),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return token;
    }

    public async Task<SecurityOperationGrant?> ClaimAsync(
        long userId,
        string grantToken,
        string purpose,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(grantToken) || string.IsNullOrWhiteSpace(purpose))
            return null;

        var hash = ComputeHash(grantToken);
        var normalizedPurpose = purpose.Trim();
        return await ClaimCoreAsync(
                query => query.Where(x => x.UserId == userId),
                hash,
                normalizedPurpose,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SecurityOperationGrant?> ClaimAsync(
        string grantToken,
        string purpose,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(grantToken) || string.IsNullOrWhiteSpace(purpose))
            return null;

        return await ClaimCoreAsync(
                static query => query,
                ComputeHash(grantToken),
                purpose.Trim(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SecurityOperationGrant?> ClaimCoreAsync(
        Func<IQueryable<SecurityOperationGrant>, IQueryable<SecurityOperationGrant>> scope,
        string hash,
        string normalizedPurpose,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var grant = await scope(db.SecurityOperationGrants)
            .FirstOrDefaultAsync(x => x.GrantHash == hash && x.Purpose == normalizedPurpose,
                cancellationToken)
            .ConfigureAwait(false);
        if (grant is null)
            return null;

        if (grant.State == SecurityOperationGrantState.Available && grant.ExpiresAt <= now)
        {
            grant.State = SecurityOperationGrantState.Expired;
            grant.CompletedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        // A process can die after ClaimAsync commits and before the business
        // mutation completes. Once the grant's absolute deadline has passed,
        // reclaim it as terminal instead of leaving a permanently stuck
        // Claimed row. The state concurrency token makes this transition
        // mutually exclusive with a late Complete/Restore call.
        if (grant.State == SecurityOperationGrantState.Claimed && grant.ExpiresAt <= now)
        {
            await db.SecurityOperationGrants
                .Where(x => x.Id == grant.Id
                            && x.State == SecurityOperationGrantState.Claimed
                            && x.ExpiresAt <= now)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.State, SecurityOperationGrantState.Expired)
                        .SetProperty(x => x.CompletedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return null;
        }

        if (grant.State != SecurityOperationGrantState.Available || grant.ExpiresAt <= now)
            return null;

        grant.State = SecurityOperationGrantState.Claimed;
        grant.ClaimedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(grant).State = EntityState.Detached;
            return null;
        }
        return grant;
    }

    public Task<bool> CompleteAsync(
        SecurityOperationGrant grant,
        CancellationToken cancellationToken = default)
        => TransitionAsync(grant, SecurityOperationGrantState.Completed, cancellationToken);

    public Task<bool> RestoreAsync(
        SecurityOperationGrant grant,
        CancellationToken cancellationToken = default)
        => TransitionAsync(grant, SecurityOperationGrantState.Restored, cancellationToken);

    private async Task<bool> TransitionAsync(
        SecurityOperationGrant grant,
        SecurityOperationGrantState state,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = await db.SecurityOperationGrants
            .Where(x => x.Id == grant.Id
                        && x.UserId == grant.UserId
                        && x.GrantHash == grant.GrantHash
                        && x.State == SecurityOperationGrantState.Claimed)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.State, state)
                    .SetProperty(x => x.CompletedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated == 1)
        {
            grant.State = state;
            grant.CompletedAt = now;
        }

        return updated == 1;
    }

    private static string ComputeHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? NormalizePayloadHash(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
