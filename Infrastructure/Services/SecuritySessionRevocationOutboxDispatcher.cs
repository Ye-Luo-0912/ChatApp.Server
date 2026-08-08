using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models.Export;
using Core.Models.Security;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Executes post-commit security cleanup. Redis is deliberately outside the
/// database transaction, but every durable state transition is fenced by the
/// queue owner/token and can be retried idempotently.
/// </summary>
public sealed class SecuritySessionRevocationOutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<SecuritySessionRevocationOutboxDispatcher> logger,
    string? ownerId = null,
    TimeSpan? leaseDuration = null,
    int maxAttempts = 8) : ILeasedJobStore<SecuritySessionRevocationOutboxItem>, IReclaimCountSource
{
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(2);
    private readonly string _ownerId = ownerId ?? CreateOwnerId();
    private readonly TimeSpan _leaseDuration = leaseDuration ?? DefaultLeaseDuration;
    private readonly int _maxAttempts = Math.Max(1, maxAttempts);
    private int _reclaimed;

    public int MaxAttempts => _maxAttempts;

    public async Task<IReadOnlyList<SecuritySessionRevocationOutboxItem>> ClaimAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTimeOffset.UtcNow;
        var reclaimed = await db.SecuritySessionRevocationOutbox
            .Where(x => x.Status == SecuritySessionRevocationOutboxStatus.Processing
                        && x.LeaseExpiresAt != null
                        && x.LeaseExpiresAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, x => x.AttemptCount + 1 >= _maxAttempts
                    ? SecuritySessionRevocationOutboxStatus.DeadLetter
                    : SecuritySessionRevocationOutboxStatus.Failed)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                .SetProperty(x => x.LastError, "security revocation lease expired")
                .SetProperty(x => x.NextAttemptAt, now)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseToken, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (reclaimed > 0)
            Interlocked.Add(ref _reclaimed, reclaimed);

        var leaseUntil = now.Add(_leaseDuration);
        var limit = Math.Clamp(maxCount, 1, 200);
        var ids = await db.Database.SqlQuery<long>($"""
            UPDATE "T_SecuritySessionRevocationOutbox" AS o
            SET "Status" = {(byte)SecuritySessionRevocationOutboxStatus.Processing},
                "LeaseOwner" = {_ownerId},
                "LeaseToken" = md5(random()::text || clock_timestamp()::text || o."Id"::text),
                "LeaseExpiresAt" = {leaseUntil},
                "UpdatedAt" = {now}
            WHERE o."Id" IN (
                SELECT i."Id"
                FROM "T_SecuritySessionRevocationOutbox" AS i
                WHERE i."Status" IN ({(byte)SecuritySessionRevocationOutboxStatus.Pending}, {(byte)SecuritySessionRevocationOutboxStatus.Failed})
                  AND i."NextAttemptAt" <= {now}
                ORDER BY i."NextAttemptAt", i."Id"
                FOR UPDATE SKIP LOCKED
                LIMIT {limit}
            )
            RETURNING o."Id"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0)
            return [];

        return await db.SecuritySessionRevocationOutbox.AsNoTracking()
            .Where(x => ids.Contains(x.Id)
                        && x.Status == SecuritySessionRevocationOutboxStatus.Processing
                        && x.LeaseOwner == _ownerId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public int ConsumeReclaimedCount()
        => Interlocked.Exchange(ref _reclaimed, 0);

    public async Task<LeaseRenewalResult> RenewAsync(
        SecuritySessionRevocationOutboxItem item,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var updated = await db.SecuritySessionRevocationOutbox
                .Where(x => x.Id == item.Id
                            && x.Status == SecuritySessionRevocationOutboxStatus.Processing
                            && x.LeaseOwner == _ownerId
                            && x.LeaseToken == item.LeaseToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.LeaseExpiresAt, DateTimeOffset.UtcNow.Add(_leaseDuration))
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken)
                .ConfigureAwait(false);
            return updated == 1 ? LeaseRenewalResult.Renewed : LeaseRenewalResult.LeaseLost;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "安全撤销 Outbox 续租失败 Id={Id}", item.Id);
            return LeaseRenewalResult.TransientFailure;
        }
    }

    public async Task ExecuteClaimedAsync(
        SecuritySessionRevocationOutboxItem item,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var current = await db.Users.AsNoTracking()
            .Where(x => x.Id == item.UserId)
            .Select(x => new { x.SecurityVersion })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
            return;

        // The durable outbox is the recovery path for a commit callback or
        // Pub/Sub notification that was lost. Invalidate the derived auth
        // snapshot from the worker as soon as the committed security version
        // is known; the database remains the authoritative fence.
        var snapshots = scope.ServiceProvider.GetRequiredService<IAuthSnapshotStore>();
        await snapshots.InvalidateAsync(
                item.UserId,
                current.SecurityVersion,
                failOnCacheError: true,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // A newer mutation supersedes the exception for the old session. In
        // that case revoke every session rather than preserving a device that
        // may have been issued under an older operation.
        var exceptDevice = current.SecurityVersion == item.ExpectedSecurityVersion
            ? item.ExceptDeviceId
            : null;
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionStore>();
        await sessions.RevokeAllSessionsAsync(
                item.UserId.ToString(), exceptDevice, cancellationToken)
            .ConfigureAwait(false);

        if (item.RevokeTrustedDevices)
        {
            var now = DateTimeOffset.UtcNow;
            await db.TrustedDevices
                .Where(x => x.UserId == item.UserId && x.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task<bool> CompleteAsync(
        SecuritySessionRevocationOutboxItem item,
        CancellationToken cancellationToken = default)
        => UpdateTerminalAsync(item, SecuritySessionRevocationOutboxStatus.Completed, null, cancellationToken);

    public Task<bool> RetryAsync(
        SecuritySessionRevocationOutboxItem item,
        string error,
        CancellationToken cancellationToken = default)
        => UpdateFailureAsync(item, error, false, cancellationToken);

    public Task<bool> DeadLetterAsync(
        SecuritySessionRevocationOutboxItem item,
        string error,
        CancellationToken cancellationToken = default)
        => UpdateFailureAsync(item, error, true, cancellationToken);

    private async Task<bool> UpdateTerminalAsync(
        SecuritySessionRevocationOutboxItem item,
        SecuritySessionRevocationOutboxStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTimeOffset.UtcNow;
        var updated = await db.SecuritySessionRevocationOutbox
            .Where(x => x.Id == item.Id
                        && x.Status == SecuritySessionRevocationOutboxStatus.Processing
                        && x.LeaseOwner == _ownerId
                        && x.LeaseToken == item.LeaseToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.LastError, error)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseToken, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    private async Task<bool> UpdateFailureAsync(
        SecuritySessionRevocationOutboxItem item,
        string error,
        bool forceDeadLetter,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTimeOffset.UtcNow;
        var attempts = forceDeadLetter ? Math.Max(_maxAttempts, item.AttemptCount + 1) : item.AttemptCount + 1;
        var status = forceDeadLetter || attempts >= _maxAttempts
            ? SecuritySessionRevocationOutboxStatus.DeadLetter
            : SecuritySessionRevocationOutboxStatus.Failed;
        var updated = await db.SecuritySessionRevocationOutbox
            .Where(x => x.Id == item.Id
                        && x.Status == SecuritySessionRevocationOutboxStatus.Processing
                        && x.LeaseOwner == _ownerId
                        && x.LeaseToken == item.LeaseToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.AttemptCount, attempts)
                .SetProperty(x => x.LastError, error.Length <= 1000 ? error : error[..1000])
                .SetProperty(x => x.NextAttemptAt, now.Add(LeasedJobBackoff.ExponentialWithJitter(
                    TimeSpan.FromSeconds(5), attempts, TimeSpan.FromMinutes(5))))
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseToken, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    private static string CreateOwnerId()
    {
        var value = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        return value[..Math.Min(128, value.Length)];
    }
}
