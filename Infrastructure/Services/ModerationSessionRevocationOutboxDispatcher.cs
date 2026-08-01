using System.Globalization;
using Core.Interfaces.Auth;
using Core.Models.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// 审核封禁会话撤销 Outbox 的领取、业务栅栏校验和 fenced 收尾。
/// Redis 撤销按删除语义幂等；若外部调用成功后进程崩溃，安全重试只会再次删除失效会话。
/// </summary>
public sealed class ModerationSessionRevocationOutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<ModerationSessionRevocationOutboxDispatcher> logger,
    string? ownerId = null,
    TimeSpan? leaseDuration = null,
    int maxAttempts = 8,
    int batchSize = 16)
{
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(2);

    private readonly string _ownerId = ownerId ?? CreateOwnerId();
    private readonly TimeSpan _leaseDuration = leaseDuration ?? DefaultLeaseDuration;
    private readonly int _maxAttempts = Math.Max(1, maxAttempts);
    private readonly int _batchSize = Math.Max(1, batchSize);

    public string OwnerId => _ownerId;

    public async Task<int> ReclaimExpiredLeasesAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTimeOffset.UtcNow;

        var reclaimed = await db.ModerationSessionRevocationOutbox
            .Where(x => x.Status == ModerationSessionRevocationOutboxStatus.Processing
                        && x.LeaseExpiresAt != null
                        && x.LeaseExpiresAt <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(
                    x => x.Status,
                    x => x.AttemptCount + 1 >= _maxAttempts
                        ? ModerationSessionRevocationOutboxStatus.Dead
                        : ModerationSessionRevocationOutboxStatus.Failed)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                .SetProperty(x => x.LastError, "Session revocation lease expired")
                .SetProperty(x => x.NextAttemptAt, now)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseToken, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);

        if (reclaimed > 0)
        {
            logger.LogWarning(
                "回收 {Count} 条过期审核会话撤销租约 Owner={OwnerId}",
                reclaimed,
                _ownerId);
        }

        return reclaimed;
    }

    public async Task<IReadOnlyList<ModerationSessionRevocationOutboxItem>> ClaimDueItemsAsync(
        int? maxItems = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTimeOffset.UtcNow;
        var leaseToken = Guid.NewGuid().ToString("N");
        var leaseExpiresAt = now.Add(_leaseDuration);
        var claimLimit = Math.Clamp(maxItems ?? _batchSize, 1, _batchSize);

        var claimedIds = await db.Database
            .SqlQuery<long>($"""
                UPDATE "T_ModerationSessionRevocationOutbox" AS o
                SET "Status" = {(byte)ModerationSessionRevocationOutboxStatus.Processing},
                    "LeaseOwner" = {_ownerId},
                    "LeaseToken" = {leaseToken},
                    "LeaseExpiresAt" = {leaseExpiresAt},
                    "UpdatedAt" = {now}
                WHERE o."Id" IN (
                    SELECT i."Id"
                    FROM "T_ModerationSessionRevocationOutbox" AS i
                    WHERE i."Status" IN (
                        {(byte)ModerationSessionRevocationOutboxStatus.Pending},
                        {(byte)ModerationSessionRevocationOutboxStatus.Failed})
                      AND i."NextAttemptAt" <= {now}
                    ORDER BY i."NextAttemptAt", i."Id"
                    FOR UPDATE SKIP LOCKED
                    LIMIT {claimLimit}
                )
                RETURNING o."Id"
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (claimedIds.Count == 0)
            return [];

        return await db.ModerationSessionRevocationOutbox
            .AsNoTracking()
            .Where(x => claimedIds.Contains(x.Id)
                        && x.Status == ModerationSessionRevocationOutboxStatus.Processing
                        && x.LeaseOwner == _ownerId
                        && x.LeaseToken == leaseToken)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ProcessItemAsync(
        ModerationSessionRevocationOutboxItem item,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        try
        {
            if (!await IsStillCurrentBanAsync(db, item, cancellationToken).ConfigureAwait(false))
            {
                await MarkSkippedAsync(db, item, cancellationToken).ConfigureAwait(false);
                return;
            }

            var sessions = scope.ServiceProvider.GetRequiredService<ISessionStore>();
            await sessions.RevokeAllSessionsAsync(
                    item.UserId.ToString(CultureInfo.InvariantCulture),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await MarkCompletedAsync(db, item, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseLeaseAsync(db, item, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "审核会话撤销 Outbox 处理失败 Id={Id} UserId={UserId}",
                item.Id,
                item.UserId);
            await MarkFailedAsync(db, item, ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> IsStillCurrentBanAsync(
        UserDbContext db,
        ModerationSessionRevocationOutboxItem item,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await (
                from outbox in db.ModerationSessionRevocationOutbox.AsNoTracking()
                join user in db.Users.AsNoTracking() on outbox.UserId equals user.Id
                where outbox.Id == item.Id
                      && outbox.Status == ModerationSessionRevocationOutboxStatus.Processing
                      && outbox.LeaseOwner == _ownerId
                      && outbox.LeaseToken == item.LeaseToken
                      && user.SecurityVersion == outbox.ExpectedSecurityVersion
                      && user.BanUntil == outbox.ExpectedBanUntil
                      && user.BanUntil > now
                select outbox.Id)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task MarkCompletedAsync(
        UserDbContext db,
        ModerationSessionRevocationOutboxItem item,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await db.ModerationSessionRevocationOutbox
            .Where(x => x.Id == item.Id
                        && x.Status == ModerationSessionRevocationOutboxStatus.Processing
                        && x.LeaseOwner == _ownerId
                        && x.LeaseToken == item.LeaseToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, ModerationSessionRevocationOutboxStatus.Completed)
                .SetProperty(x => x.LastError, (string?)null)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseToken, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task MarkSkippedAsync(
        UserDbContext db,
        ModerationSessionRevocationOutboxItem item,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var skipped = await db.ModerationSessionRevocationOutbox
            .Where(x => x.Id == item.Id
                        && x.Status == ModerationSessionRevocationOutboxStatus.Processing
                        && x.LeaseOwner == _ownerId
                        && x.LeaseToken == item.LeaseToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, ModerationSessionRevocationOutboxStatus.Skipped)
                .SetProperty(x => x.LastError, "Superseded security version or inactive ban")
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseToken, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);

        if (skipped == 1)
        {
            logger.LogInformation(
                "跳过过期审核会话撤销 Outbox Id={Id} UserId={UserId}",
                item.Id,
                item.UserId);
        }
    }

    private async Task MarkFailedAsync(
        UserDbContext db,
        ModerationSessionRevocationOutboxItem item,
        string error,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var attempts = item.AttemptCount + 1;
        var dead = attempts >= _maxAttempts;
        var nextAttemptAt = now.Add(ComputeBackoff(attempts));
        var message = error.Length <= 1000 ? error : error[..1000];

        await db.ModerationSessionRevocationOutbox
            .Where(x => x.Id == item.Id
                        && x.Status == ModerationSessionRevocationOutboxStatus.Processing
                        && x.LeaseOwner == _ownerId
                        && x.LeaseToken == item.LeaseToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(
                    x => x.Status,
                    dead
                        ? ModerationSessionRevocationOutboxStatus.Dead
                        : ModerationSessionRevocationOutboxStatus.Failed)
                .SetProperty(x => x.AttemptCount, attempts)
                .SetProperty(x => x.LastError, message)
                .SetProperty(x => x.NextAttemptAt, nextAttemptAt)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseToken, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ReleaseLeaseAsync(
        UserDbContext db,
        ModerationSessionRevocationOutboxItem item,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await db.ModerationSessionRevocationOutbox
            .Where(x => x.Id == item.Id
                        && x.Status == ModerationSessionRevocationOutboxStatus.Processing
                        && x.LeaseOwner == _ownerId
                        && x.LeaseToken == item.LeaseToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, ModerationSessionRevocationOutboxStatus.Pending)
                .SetProperty(x => x.NextAttemptAt, now)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseToken, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
    }

    private static TimeSpan ComputeBackoff(int attemptCount)
        => TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(attemptCount - 1, 6)) * 5));

    private static string CreateOwnerId()
    {
        var value = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        return value[..Math.Min(128, value.Length)];
    }
}
