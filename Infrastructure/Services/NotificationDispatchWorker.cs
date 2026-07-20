using Core.Interfaces;
using Core.Models.Notifications;
using Core.Models.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class NotificationOutboxDispatcher(
    UserDbContext db,
    IEmailSender emailSender,
    ILogger logger)
{
    private static readonly string InstanceId = Environment.MachineName + ":" + Guid.NewGuid().ToString("N")[..8];
    private const int MaxAttempts = 8;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public async Task ReclaimExpiredLeasesAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - Lease;
        await db.NotificationOutbox
            .Where(x => x.Status == NotificationOutboxStatus.Processing && x.LockedAt != null && x.LockedAt < cutoff)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status, NotificationOutboxStatus.Failed)
                    .SetProperty(x => x.LockedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.LockOwner, (string?)null)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow)
                    .SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow),
                cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationOutboxItem>> ClaimDueItemsAsync(
        int batchSize, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        List<long> ids;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            ids = await db.Database
                .SqlQuery<long>($"""
                    UPDATE "T_NotificationOutbox" AS o
                    SET "Status" = {(int)NotificationOutboxStatus.Processing},
                        "LockedAt" = {now},
                        "LockOwner" = {InstanceId},
                        "UpdatedAt" = {now}
                    WHERE o."Id" IN (
                        SELECT i."Id" FROM "T_NotificationOutbox" AS i
                        WHERE i."Status" IN ({(int)NotificationOutboxStatus.Pending}, {(int)NotificationOutboxStatus.Failed})
                          AND i."NextAttemptAt" <= {now}
                        ORDER BY i."NextAttemptAt"
                        FOR UPDATE SKIP LOCKED
                        LIMIT {batchSize}
                    )
                    RETURNING o."Id"
                    """)
                .ToListAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        if (ids.Count == 0) return [];
        return await db.NotificationOutbox.Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
    }

    public async Task ProcessItemAsync(NotificationOutboxItem item, CancellationToken cancellationToken)
    {
        try
        {
            if (item.InAppDeliveredAt is null)
            {
                var already = await db.InAppNotifications.AsNoTracking()
                    .AnyAsync(n => n.SourceOutboxId == item.Id, cancellationToken);
                if (!already)
                {
                    db.InAppNotifications.Add(new InAppNotification
                    {
                        UserId = item.UserId,
                        Type = item.Type,
                        Title = item.Title,
                        Body = item.Body,
                        CreatedAt = DateTimeOffset.UtcNow,
                        SourceOutboxId = item.Id,
                    });
                    try
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    catch (DbUpdateException)
                    {
                        // 唯一约束冲突：并发重试已写入
                        db.ChangeTracker.Clear();
                    }
                }

                await db.NotificationOutbox
                    .Where(x => x.Id == item.Id && x.InAppDeliveredAt == null)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.InAppDeliveredAt, DateTimeOffset.UtcNow)
                            .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
                        cancellationToken);
                item.InAppDeliveredAt = DateTimeOffset.UtcNow;
            }

            if (item.PreferEmail && item.EmailDeliveredAt is null)
            {
                var user = await db.Users.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == item.UserId, cancellationToken);
                if (user is { NotifySecurityEmail: true } && !string.IsNullOrWhiteSpace(user.Email))
                {
                    await emailSender.SendEmailAsync(
                        user.Email,
                        $"[ChatApp] {item.Title}",
                        $"<p>{item.Body}</p><p>如非本人操作，请立即修改密码并检查登录设备。</p>",
                        isHtml: true,
                        cancellationToken);
                }

                await db.NotificationOutbox
                    .Where(x => x.Id == item.Id && x.EmailDeliveredAt == null)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.EmailDeliveredAt, DateTimeOffset.UtcNow)
                            .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
                        cancellationToken);
                item.EmailDeliveredAt = DateTimeOffset.UtcNow;
            }

            await db.NotificationOutbox
                .Where(x => x.Id == item.Id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.Status, NotificationOutboxStatus.Sent)
                        .SetProperty(x => x.LockedAt, (DateTimeOffset?)null)
                        .SetProperty(x => x.LockOwner, (string?)null)
                        .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow)
                        .SetProperty(x => x.LastError, (string?)null),
                    cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "通知 Outbox 处理失败 Id={Id}", item.Id);
            var attempts = item.AttemptCount + 1;
            var dead = attempts >= MaxAttempts;
            var delay = TimeSpan.FromSeconds(Math.Min(3600, Math.Pow(2, attempts) * 5));
            await db.NotificationOutbox
                .Where(x => x.Id == item.Id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.Status, dead ? NotificationOutboxStatus.Dead : NotificationOutboxStatus.Failed)
                        .SetProperty(x => x.AttemptCount, attempts)
                        .SetProperty(x => x.LastError, ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message)
                        .SetProperty(x => x.LockedAt, (DateTimeOffset?)null)
                        .SetProperty(x => x.LockOwner, (string?)null)
                        .SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.Add(delay))
                        .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
                    cancellationToken);
        }
    }
}

public sealed class NotificationDispatchWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<NotificationDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
                var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
                var dispatcher = new NotificationOutboxDispatcher(db, email, logger);
                await dispatcher.ReclaimExpiredLeasesAsync(stoppingToken);
                var items = await dispatcher.ClaimDueItemsAsync(20, stoppingToken);
                foreach (var item in items)
                    await dispatcher.ProcessItemAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "通知 Outbox 轮询异常");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
