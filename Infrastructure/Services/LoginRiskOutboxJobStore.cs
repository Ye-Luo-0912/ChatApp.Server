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
/// Fenced durable store for post-login risk analysis. A database row, rather
/// than an API-process channel, is the recovery and cross-instance boundary.
/// </summary>
public sealed class LoginRiskOutboxJobStore(
    IServiceScopeFactory scopeFactory,
    ILogger<LoginRiskOutboxJobStore> logger) :
    ILeasedJobStore<LoginRiskOutboxItem>,
    IReclaimCountSource
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    public const int MaxAttempts = 8;

    private readonly string _ownerId = CreateOwnerId();
    private int _reclaimed;

    public async Task<IReadOnlyList<LoginRiskOutboxItem>> ClaimAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTimeOffset.UtcNow;

        var reclaimed = await db.LoginRiskOutbox
            .Where(x => x.Status == LoginRiskOutboxStatus.Processing
                        && x.LeaseExpiresAt != null
                        && x.LeaseExpiresAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, x => x.AttemptCount + 1 >= MaxAttempts
                    ? LoginRiskOutboxStatus.DeadLetter
                    : LoginRiskOutboxStatus.Failed)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                .SetProperty(x => x.LastError, "login risk lease expired")
                .SetProperty(x => x.NextAttemptAt, now)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseToken, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.CompletedAt, x => x.AttemptCount + 1 >= MaxAttempts
                    ? now
                    : (DateTimeOffset?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (reclaimed > 0)
            Interlocked.Add(ref _reclaimed, reclaimed);

        maxCount = Math.Clamp(maxCount, 1, 200);
        var leaseUntil = now.Add(LeaseDuration);
        var ids = await db.Database.SqlQuery<long>($"""
            UPDATE "T_LoginRiskOutbox" AS o
            SET "Status" = {(byte)LoginRiskOutboxStatus.Processing},
                "LeaseOwner" = {_ownerId},
                "LeaseToken" = md5(random()::text || clock_timestamp()::text || o."Id"::text),
                "LeaseExpiresAt" = {leaseUntil},
                "UpdatedAt" = {now}
            WHERE o."Id" IN (
                SELECT i."Id"
                FROM "T_LoginRiskOutbox" AS i
                WHERE i."Status" IN ({(byte)LoginRiskOutboxStatus.Pending}, {(byte)LoginRiskOutboxStatus.Failed})
                  AND i."NextAttemptAt" <= {now}
                ORDER BY i."NextAttemptAt", i."Id"
                FOR UPDATE SKIP LOCKED
                LIMIT {maxCount}
            )
            RETURNING o."Id"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (ids.Count == 0)
        {
            await RecordBacklogAsync(db, now, cancellationToken).ConfigureAwait(false);
            return [];
        }

        await RecordBacklogAsync(db, now, cancellationToken).ConfigureAwait(false);

        return await db.LoginRiskOutbox.AsNoTracking()
            .Where(x => ids.Contains(x.Id)
                        && x.Status == LoginRiskOutboxStatus.Processing
                        && x.LeaseOwner == _ownerId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LeaseRenewalResult> RenewAsync(
        LoginRiskOutboxItem item,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var updated = await db.LoginRiskOutbox
                .Where(x => x.Id == item.Id
                            && x.Status == LoginRiskOutboxStatus.Processing
                            && x.LeaseOwner == _ownerId
                            && x.LeaseToken == item.LeaseToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.LeaseExpiresAt, DateTimeOffset.UtcNow.Add(LeaseDuration))
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
            logger.LogDebug(ex, "登录风险租约续租失败 Id={Id}", item.Id);
            return LeaseRenewalResult.TransientFailure;
        }
    }

    public async Task ExecuteClaimedAsync(
        LoginRiskOutboxItem item,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var analyzer = scope.ServiceProvider.GetRequiredService<LoginRiskAnalyzer>();
        await analyzer.AnalyzeAsync(item, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> CompleteAsync(LoginRiskOutboxItem item, CancellationToken cancellationToken = default)
        => UpdateTerminalAsync(item, LoginRiskOutboxStatus.Completed, null, cancellationToken);

    public Task<bool> RetryAsync(LoginRiskOutboxItem item, string error, CancellationToken cancellationToken = default)
        => UpdateFailureAsync(item, error, forceDeadLetter: false, cancellationToken);

    public Task<bool> DeadLetterAsync(LoginRiskOutboxItem item, string error, CancellationToken cancellationToken = default)
        => UpdateFailureAsync(item, error, forceDeadLetter: true, cancellationToken);

    public int ConsumeReclaimedCount()
        => Interlocked.Exchange(ref _reclaimed, 0);

    private async Task<bool> UpdateTerminalAsync(
        LoginRiskOutboxItem item,
        LoginRiskOutboxStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTimeOffset.UtcNow;
        var updated = await db.LoginRiskOutbox
            .Where(x => x.Id == item.Id
                        && x.Status == LoginRiskOutboxStatus.Processing
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
        if (updated == 1)
            AuthSecurityMetrics.RecordRisk(status == LoginRiskOutboxStatus.Completed
                ? "analysis_completed"
                : "analysis_dead_lettered");
        return updated == 1;
    }

    private async Task<bool> UpdateFailureAsync(
        LoginRiskOutboxItem item,
        string error,
        bool forceDeadLetter,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTimeOffset.UtcNow;
        var attempts = forceDeadLetter
            ? Math.Max(MaxAttempts, item.AttemptCount + 1)
            : item.AttemptCount + 1;
        var status = forceDeadLetter || attempts >= MaxAttempts
            ? LoginRiskOutboxStatus.DeadLetter
            : LoginRiskOutboxStatus.Failed;
        var updated = await db.LoginRiskOutbox
            .Where(x => x.Id == item.Id
                        && x.Status == LoginRiskOutboxStatus.Processing
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
                .SetProperty(x => x.CompletedAt, status == LoginRiskOutboxStatus.DeadLetter
                    ? now
                    : (DateTimeOffset?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    private static string CreateOwnerId()
    {
        var value = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        return value[..Math.Min(128, value.Length)];
    }

    private static async Task RecordBacklogAsync(
        UserDbContext db,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var due = db.LoginRiskOutbox
            .Where(x => (x.Status == LoginRiskOutboxStatus.Pending
                         || x.Status == LoginRiskOutboxStatus.Failed)
                        && x.NextAttemptAt <= now);
        var count = await due.CountAsync(cancellationToken).ConfigureAwait(false);
        var oldest = await due.OrderBy(x => x.CreatedAt)
            .Select(x => (DateTimeOffset?)x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        AuthSecurityMetrics.SetLoginRiskBacklog(count, oldest);
    }
}
