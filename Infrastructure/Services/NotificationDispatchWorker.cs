using Core.Interfaces;
using Core.Models.Notifications;
using Core.Models.Security;
using Core.Settings;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public sealed class NotificationOutboxDispatcher(
    UserDbContext db,
    IEmailSender emailSender,
    NotificationOutboxMetrics metrics,
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

    public async Task<long> CountBacklogAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.NotificationOutbox.AsNoTracking()
            .CountAsync(
                x => (x.Status == NotificationOutboxStatus.Pending || x.Status == NotificationOutboxStatus.Failed)
                     && x.NextAttemptAt <= now,
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
        metrics.RecordClaimed(ids.Count);
        return await db.NotificationOutbox.Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
    }

    /// <summary>批量写入站内通知，减少逐条 SaveChanges。</summary>
    public async Task DeliverInAppBatchAsync(
        IReadOnlyList<NotificationOutboxItem> items, CancellationToken cancellationToken)
    {
        var pending = items.Where(i => i.InAppDeliveredAt is null).ToList();
        if (pending.Count == 0) return;

        var ids = pending.Select(i => i.Id).ToList();
        var existing = await db.InAppNotifications.AsNoTracking()
            .Where(n => n.SourceOutboxId != null && ids.Contains(n.SourceOutboxId.Value))
            .Select(n => n.SourceOutboxId!.Value)
            .ToListAsync(cancellationToken);
        var existingSet = existing.ToHashSet();

        var toInsert = pending.Where(i => !existingSet.Contains(i.Id)).ToList();
        if (toInsert.Count > 0)
        {
            foreach (var item in toInsert)
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
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
            }
        }

        var now = DateTimeOffset.UtcNow;
        await db.NotificationOutbox
            .Where(x => ids.Contains(x.Id) && x.InAppDeliveredAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.InAppDeliveredAt, now)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);

        foreach (var item in pending)
            item.InAppDeliveredAt = now;
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
            metrics.RecordSent();
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
            if (dead) metrics.RecordDead();
            else metrics.RecordFailed();
        }
    }
}

public sealed class NotificationDispatchWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationOutboxOptions> options,
    NotificationOutboxMetrics metrics,
    ILogger<NotificationDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        var concurrency = Math.Max(1, opts.MaxConcurrency);
        var batchSize = Math.Max(1, opts.BatchSize);
        var poll = TimeSpan.FromSeconds(Math.Max(1, opts.PollIntervalSeconds));
        var backlogEvery = TimeSpan.FromSeconds(Math.Max(5, opts.BacklogSampleSeconds));
        var lastBacklogSample = DateTimeOffset.MinValue;

        using var semaphore = new SemaphoreSlim(concurrency, concurrency);
        var inFlight = new List<Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
                var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
                var dispatcher = new NotificationOutboxDispatcher(db, email, metrics, logger);
                await dispatcher.ReclaimExpiredLeasesAsync(stoppingToken);

                if (DateTimeOffset.UtcNow - lastBacklogSample >= backlogEvery)
                {
                    metrics.SetBacklog(await dispatcher.CountBacklogAsync(stoppingToken));
                    lastBacklogSample = DateTimeOffset.UtcNow;
                }

                var items = await dispatcher.ClaimDueItemsAsync(batchSize, stoppingToken);
                if (items.Count > 0)
                {
                    // 站内通知批量落库（同 scope），邮件/收尾用有界并发
                    await dispatcher.DeliverInAppBatchAsync(items, stoppingToken);

                    foreach (var item in items)
                    {
                        await semaphore.WaitAsync(stoppingToken);
                        inFlight.Add(ProcessOneAsync(item, semaphore, stoppingToken));
                    }

                    inFlight.RemoveAll(static t => t.IsCompleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "通知 Outbox 轮询异常");
            }

            try
            {
                await Task.Delay(poll, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        await Task.WhenAll(inFlight);
    }

    private async Task ProcessOneAsync(
        NotificationOutboxItem item, SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
            var dispatcher = new NotificationOutboxDispatcher(db, email, metrics, logger);
            await dispatcher.ProcessItemAsync(item, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
