using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models.Auth;
using Core.Models.Common;
using Core.Models.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class AccountLifecycleService(
    UserDbContext db,
    ISessionStore sessionStore,
    ISecurityEventStore securityEventStore,
    ILogger<AccountLifecycleService> logger) : IAccountLifecycleService
{
    public static readonly TimeSpan CoolDown = TimeSpan.FromDays(14);
    private static readonly string InstanceId = Environment.MachineName + ":" + Guid.NewGuid().ToString("N")[..8];
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    public async Task<AuthOperationResult> ScheduleDeletionAsync(
        long userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return AuthOperationResult.Fail("NotFound", "用户不存在");

        user.DeletionScheduledAt = DateTimeOffset.UtcNow.Add(CoolDown);
        user.DeletionLeaseUntil = null;
        user.DeletionLeaseOwner = null;
        user.SecurityStamp = Guid.NewGuid().ToString();
        await db.SaveChangesAsync(cancellationToken);
        await sessionStore.RevokeAllSessionsAsync(userId.ToString(), cancellationToken: cancellationToken);
        await securityEventStore.RecordAsync(
            userId, SecurityEventType.AccountDeletionScheduled,
            detail: $"scheduled={user.DeletionScheduledAt:O}",
            cancellationToken: cancellationToken);

        logger.LogWarning("用户 {UserId} 已预约注销，冷静期至 {At}", userId, user.DeletionScheduledAt);
        return AuthOperationResult.Success();
    }

    public async Task<AuthOperationResult> CancelDeletionAsync(
        long userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return AuthOperationResult.Fail("NotFound", "用户不存在");
        if (user.DeletionScheduledAt is null)
            return AuthOperationResult.Fail("NotScheduled", "未预约注销");

        user.DeletionScheduledAt = null;
        user.DeletionLeaseUntil = null;
        user.DeletionLeaseOwner = null;
        await db.SaveChangesAsync(cancellationToken);
        return AuthOperationResult.Success();
    }

    public async Task<UserDataExportDto?> ExportAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null) return null;

        var events = await db.SecurityEvents.AsNoTracking()
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.Id)
            .Take(500)
            .Select(e => new { e.Id, e.EventType, e.DeviceId, e.ClientIp, e.Detail, e.CreatedAt })
            .ToListAsync(cancellationToken);

        var friendIds = await db.Friendships.AsNoTracking()
            .Where(f => f.UserId == userId)
            .Select(f => f.FriendId)
            .ToListAsync(cancellationToken);

        return new UserDataExportDto
        {
            UserId = user.Id,
            UserName = user.UserName,
            Email = user.Email,
            Signature = user.Signature,
            Region = user.Region,
            CreatedDate = user.CreatedDate,
            SecurityEvents = events.Cast<object>().ToList(),
            FriendIds = friendIds.Cast<object>().ToList(),
        };
    }

    public async Task<int> ProcessDueDeletionsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(LeaseDuration);

        List<long> claimedIds;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            claimedIds = await db.Database
                .SqlQuery<long>($"""
                    UPDATE "AspNetUsers" AS u
                    SET "DeletionLeaseUntil" = {leaseUntil},
                        "DeletionLeaseOwner" = {InstanceId}
                    WHERE u."Id" IN (
                        SELECT i."Id" FROM "AspNetUsers" AS i
                        WHERE i."DeletionScheduledAt" IS NOT NULL
                          AND i."DeletionScheduledAt" <= {now}
                          AND (i."DeletionLeaseUntil" IS NULL OR i."DeletionLeaseUntil" < {now})
                        ORDER BY i."DeletionScheduledAt"
                        FOR UPDATE SKIP LOCKED
                        LIMIT 50
                    )
                    RETURNING u."Id"
                    """)
                .ToListAsync(cancellationToken);

            await tx.CommitAsync(cancellationToken);
        }

        if (claimedIds.Count == 0) return 0;

        var processed = 0;
        foreach (var userId in claimedIds)
        {
            try
            {
                await sessionStore.RevokeAllSessionsAsync(userId.ToString(), cancellationToken: cancellationToken);

                // 级联清理用户相关数据（策略：物理删除，非匿名化）
                await db.Friendships
                    .Where(f => f.UserId == userId || f.FriendId == userId)
                    .ExecuteDeleteAsync(cancellationToken);
                await db.FriendRequests
                    .Where(r => r.RequesterId == userId || r.TargetUserId == userId)
                    .ExecuteDeleteAsync(cancellationToken);
                await db.BlockRecords
                    .Where(b => b.BlockerId == userId || b.BlockedUserId == userId)
                    .ExecuteDeleteAsync(cancellationToken);
                await db.FriendGroups
                    .Where(g => g.UserId == userId)
                    .ExecuteDeleteAsync(cancellationToken);
                await db.InAppNotifications
                    .Where(n => n.UserId == userId)
                    .ExecuteDeleteAsync(cancellationToken);
                await db.NotificationOutbox
                    .Where(n => n.UserId == userId)
                    .ExecuteDeleteAsync(cancellationToken);
                await db.SecurityEvents
                    .Where(e => e.UserId == userId)
                    .ExecuteDeleteAsync(cancellationToken);
                await db.TrustedDevices
                    .Where(d => d.UserId == userId)
                    .ExecuteDeleteAsync(cancellationToken);
                await db.UserReports
                    .Where(r => r.ReporterId == userId || r.TargetUserId == userId)
                    .ExecuteDeleteAsync(cancellationToken);
                await db.UserRoles
                    .Where(ur => ur.UserId == userId)
                    .ExecuteDeleteAsync(cancellationToken);

                var deleted = await db.Users
                    .Where(u => u.Id == userId
                                && u.DeletionScheduledAt != null
                                && u.DeletionScheduledAt <= now
                                && u.DeletionLeaseOwner == InstanceId)
                    .ExecuteDeleteAsync(cancellationToken);
                if (deleted > 0) processed++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "注销用户 {UserId} 失败，释放租约以便重试", userId);
                await db.Users
                    .Where(u => u.Id == userId && u.DeletionLeaseOwner == InstanceId)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(u => u.DeletionLeaseUntil, (DateTimeOffset?)null)
                            .SetProperty(u => u.DeletionLeaseOwner, (string?)null),
                        cancellationToken);
            }
        }

        if (processed > 0)
            logger.LogWarning("已执行 {Count} 个到期账号注销", processed);
        return processed;
    }
}

public sealed class AccountDeletionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<AccountDeletionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var svc = scope.ServiceProvider.GetRequiredService<IAccountLifecycleService>();
                await svc.ProcessDueDeletionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "账号注销后台任务失败");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}

public sealed class NotificationQuery(UserDbContext db) : INotificationQuery
{
    public async Task<CursorPage<InAppNotificationDto>> ListAsync(
        long userId, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(limit, 1, 100);
        long? cursorId = long.TryParse(cursor, out var c) ? c : null;
        var query = db.InAppNotifications.AsNoTracking().Where(n => n.UserId == userId);
        if (cursorId.HasValue) query = query.Where(n => n.Id < cursorId.Value);

        var rows = await query.OrderByDescending(n => n.Id).Take(pageSize + 1)
            .Select(n => new InAppNotificationDto
            {
                Id = n.Id,
                Type = n.Type,
                Title = n.Title,
                Body = n.Body,
                IsRead = n.IsRead,
                CreatedAt = n.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new CursorPage<InAppNotificationDto>
        {
            Items = rows,
            HasMore = hasMore,
            NextCursor = hasMore && rows.Count > 0 ? rows[^1].Id.ToString() : null,
        };
    }

    public async Task MarkReadAsync(long userId, long notificationId, CancellationToken cancellationToken = default)
    {
        await db.InAppNotifications
            .Where(n => n.Id == notificationId && n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(
                s => s.SetProperty(n => n.IsRead, true),
                cancellationToken);
    }

    public Task<int> CountUnreadAsync(long userId, CancellationToken cancellationToken = default)
        => db.InAppNotifications.AsNoTracking()
            .CountAsync(n => n.UserId == userId && !n.IsRead, cancellationToken);

    public async Task<int> MarkReadBatchAsync(
        long userId, IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return 0;
        var distinct = ids.Distinct().Take(100).ToArray();
        return await db.InAppNotifications
            .Where(n => n.UserId == userId && distinct.Contains(n.Id) && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), cancellationToken);
    }
}
