using Core.Interfaces;
using Core.Models.Export;
using Core.Models.Security;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Fenced store for login-audit outbox rows. The durable event insert is
/// idempotent through SecurityEvent.SourceLoginAuditOutboxId, so a crash
/// between event insertion and queue completion can safely be retried.
/// </summary>
public sealed class LoginAuditOutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<LoginAuditOutboxDispatcher> logger,
    string? ownerId = null,
    TimeSpan? leaseDuration = null,
    int maxAttempts = 8) : ILeasedJobStore<LoginAuditOutboxItem>, IReclaimCountSource
{
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(2);
    private readonly string _ownerId = ownerId ?? CreateOwnerId();
    private readonly TimeSpan _leaseDuration = leaseDuration ?? DefaultLeaseDuration;
    private readonly int _maxAttempts = Math.Max(1, maxAttempts);
    private int _reclaimed;

    public int MaxAttempts => _maxAttempts;

    private static string CreateOwnerId()
    {
        var value = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        return value[..Math.Min(128, value.Length)];
    }

    public async Task<IReadOnlyList<LoginAuditOutboxItem>> ClaimAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTimeOffset.UtcNow;

        var reclaimed = await db.LoginAuditOutbox
            .Where(x => x.Status == LoginAuditOutboxStatus.Processing
                        && x.LeaseExpiresAt != null
                        && x.LeaseExpiresAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, x => x.AttemptCount + 1 >= _maxAttempts
                    ? LoginAuditOutboxStatus.DeadLetter
                    : LoginAuditOutboxStatus.Failed)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                .SetProperty(x => x.LastError, "login audit lease expired")
                .SetProperty(x => x.NextAttemptAt, now)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseToken, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.CompletedAt, x => x.AttemptCount + 1 >= _maxAttempts
                    ? now
                    : (DateTimeOffset?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (reclaimed > 0)
            Interlocked.Add(ref _reclaimed, reclaimed);

        maxCount = Math.Clamp(maxCount, 1, 200);
        var leaseUntil = now.Add(_leaseDuration);
        var ids = await db.Database.SqlQuery<long>($"""
            UPDATE "T_LoginAuditOutbox" AS o
            SET "Status" = {(byte)LoginAuditOutboxStatus.Processing},
                "LeaseOwner" = {_ownerId},
                "LeaseToken" = md5(random()::text || clock_timestamp()::text || o."Id"::text),
                "LeaseExpiresAt" = {leaseUntil},
                "UpdatedAt" = {now}
            WHERE o."Id" IN (
                SELECT i."Id"
                FROM "T_LoginAuditOutbox" AS i
                WHERE i."Status" IN ({(byte)LoginAuditOutboxStatus.Pending}, {(byte)LoginAuditOutboxStatus.Failed})
                  AND i."NextAttemptAt" <= {now}
                ORDER BY i."NextAttemptAt", i."Id"
                FOR UPDATE SKIP LOCKED
                LIMIT {maxCount}
            )
            RETURNING o."Id"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (ids.Count == 0)
            return [];

        return await db.LoginAuditOutbox.AsNoTracking()
            .Where(x => ids.Contains(x.Id)
                        && x.Status == LoginAuditOutboxStatus.Processing
                        && x.LeaseOwner == _ownerId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LeaseRenewalResult> RenewAsync(
        LoginAuditOutboxItem item,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var now = DateTimeOffset.UtcNow;
            var updated = await db.LoginAuditOutbox
                .Where(x => x.Id == item.Id
                            && x.Status == LoginAuditOutboxStatus.Processing
                            && x.LeaseOwner == _ownerId
                            && x.LeaseToken == item.LeaseToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.LeaseExpiresAt, now.Add(_leaseDuration))
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false);
            return updated == 1 ? LeaseRenewalResult.Renewed : LeaseRenewalResult.LeaseLost;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "登录审计租约续租失败 Id={Id}", item.Id);
            return LeaseRenewalResult.TransientFailure;
        }
    }

    public async Task ExecuteClaimedAsync(
        LoginAuditOutboxItem item,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var owned = await db.LoginAuditOutbox.AsNoTracking()
            .AnyAsync(x => x.Id == item.Id
                           && x.Status == LoginAuditOutboxStatus.Processing
                           && x.LeaseOwner == _ownerId
                           && x.LeaseToken == item.LeaseToken, cancellationToken)
            .ConfigureAwait(false);
        if (!owned)
            throw new InvalidOperationException("登录审计租约已失效");

        var exists = await db.SecurityEvents.AnyAsync(
                x => x.SourceLoginAuditOutboxId == item.Id,
                cancellationToken)
            .ConfigureAwait(false);
        if (exists)
            return;

        db.SecurityEvents.Add(new SecurityEvent
        {
            UserId = item.UserId,
            EventType = item.EventType,
            DeviceId = item.DeviceId,
            SessionId = item.SessionId,
            ClientIp = item.ClientIp,
            Location = item.Location,
            Detail = item.Detail,
            ActorUserId = item.ActorUserId,
            SourceLoginAuditOutboxId = item.Id,
            CreatedAt = item.CreatedAt,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> CompleteAsync(LoginAuditOutboxItem item, CancellationToken cancellationToken = default)
        => UpdateTerminalAsync(item, LoginAuditOutboxStatus.Completed, null, cancellationToken);

    public Task<bool> RetryAsync(LoginAuditOutboxItem item, string error, CancellationToken cancellationToken = default)
        => UpdateFailureAsync(item, error, forceDeadLetter: false, cancellationToken);

    public Task<bool> DeadLetterAsync(LoginAuditOutboxItem item, string error, CancellationToken cancellationToken = default)
        => UpdateFailureAsync(item, error, forceDeadLetter: true, cancellationToken);

    public int ConsumeReclaimedCount()
        => Interlocked.Exchange(ref _reclaimed, 0);

    private async Task<bool> UpdateTerminalAsync(
        LoginAuditOutboxItem item,
        LoginAuditOutboxStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTimeOffset.UtcNow;
        var updated = await db.LoginAuditOutbox
            .Where(x => x.Id == item.Id
                        && x.Status == LoginAuditOutboxStatus.Processing
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
        LoginAuditOutboxItem item,
        string error,
        bool forceDeadLetter,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTimeOffset.UtcNow;
        var attempts = forceDeadLetter ? Math.Max(_maxAttempts, item.AttemptCount + 1) : item.AttemptCount + 1;
        var status = forceDeadLetter || attempts >= _maxAttempts
            ? LoginAuditOutboxStatus.DeadLetter
            : LoginAuditOutboxStatus.Failed;
        var updated = await db.LoginAuditOutbox
            .Where(x => x.Id == item.Id
                        && x.Status == LoginAuditOutboxStatus.Processing
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
                .SetProperty(x => x.CompletedAt, status == LoginAuditOutboxStatus.DeadLetter
                    ? now
                    : (DateTimeOffset?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }
}
