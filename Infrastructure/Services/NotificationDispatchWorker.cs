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
using Npgsql;
using NpgsqlTypes;

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

        await InsertInAppNotificationsAsync(pending, cancellationToken);

        var ids = pending.Select(i => i.Id).ToList();
        var now = DateTimeOffset.UtcNow;
        var updated = await db.NotificationOutbox
            .Where(x => ids.Contains(x.Id)
                && x.InAppDeliveredAt == null
                && x.Status == NotificationOutboxStatus.Processing
                && x.LockOwner == InstanceId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.InAppDeliveredAt, now)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);

        if (updated != pending.Count)
            throw new InvalidOperationException("通知 Outbox 租约已失效，停止处理当前批次");

        foreach (var item in pending)
            item.InAppDeliveredAt = now;
    }

    public async Task ProcessItemAsync(NotificationOutboxItem item, CancellationToken cancellationToken)
    {
        try
        {
            if (item.InAppDeliveredAt is null)
            {
                await InsertInAppNotificationsAsync([item], cancellationToken);

                var deliveredAt = DateTimeOffset.UtcNow;
                var updated = await db.NotificationOutbox
                    .Where(x => x.Id == item.Id
                        && x.InAppDeliveredAt == null
                        && x.Status == NotificationOutboxStatus.Processing
                        && x.LockOwner == InstanceId
                        && x.LockedAt == item.LockedAt)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.InAppDeliveredAt, deliveredAt)
                            .SetProperty(x => x.UpdatedAt, deliveredAt),
                        cancellationToken);
                if (updated == 0) return;
                item.InAppDeliveredAt = deliveredAt;
            }

            if (item.PreferEmail && item.EmailDeliveredAt is null)
            {
                var renewedAt = DateTimeOffset.UtcNow;
                var renewed = await db.NotificationOutbox
                    .Where(x => x.Id == item.Id
                        && x.Status == NotificationOutboxStatus.Processing
                        && x.LockOwner == InstanceId
                        && x.LockedAt == item.LockedAt)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.LockedAt, renewedAt)
                            .SetProperty(x => x.UpdatedAt, renewedAt),
                        cancellationToken);
                if (renewed == 0) return;
                item.LockedAt = renewedAt;

                var user = await db.Users.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == item.UserId, cancellationToken);
                if (user is { NotifySecurityEmail: true } && !string.IsNullOrWhiteSpace(user.Email))
                {
                    var email = await emailSender.EnqueueEmailAsync(
                        user.Email,
                        $"[ChatApp] {item.Title}",
                        $"<p>{item.Body}</p><p>如非本人操作，请立即修改密码并检查登录设备。</p>",
                        isHtml: true,
                        emailType: "SecurityNotification",
                        idempotencyKey: $"notification:{item.Id}",
                        cancellationToken);
                    if (!email.IsSuccess)
                        throw new InvalidOperationException(email.ErrorMessage ?? "安全通知邮件入队失败");
                }

                var deliveredAt = DateTimeOffset.UtcNow;
                var updated = await db.NotificationOutbox
                    .Where(x => x.Id == item.Id
                        && x.EmailDeliveredAt == null
                        && x.Status == NotificationOutboxStatus.Processing
                        && x.LockOwner == InstanceId
                        && x.LockedAt == item.LockedAt)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.EmailDeliveredAt, deliveredAt)
                            .SetProperty(x => x.UpdatedAt, deliveredAt),
                        cancellationToken);
                if (updated == 0) return;
                item.EmailDeliveredAt = deliveredAt;
            }

            var sent = await db.NotificationOutbox
                .Where(x => x.Id == item.Id
                    && x.Status == NotificationOutboxStatus.Processing
                    && x.LockOwner == InstanceId
                    && x.LockedAt == item.LockedAt)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.Status, NotificationOutboxStatus.Sent)
                        .SetProperty(x => x.LockedAt, (DateTimeOffset?)null)
                        .SetProperty(x => x.LockOwner, (string?)null)
                        .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow)
                        .SetProperty(x => x.LastError, (string?)null),
                    cancellationToken);
            if (sent == 1)
                metrics.RecordSent();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseLeaseAsync(item, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "通知 Outbox 处理失败 Id={Id}", item.Id);
            var attempts = item.AttemptCount + 1;
            var dead = attempts >= MaxAttempts;
            var delay = TimeSpan.FromSeconds(Math.Min(3600, Math.Pow(2, attempts) * 5));
            await db.NotificationOutbox
                .Where(x => x.Id == item.Id
                    && x.Status == NotificationOutboxStatus.Processing
                    && x.LockOwner == InstanceId
                    && x.LockedAt == item.LockedAt)
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

    private async Task InsertInAppNotificationsAsync(
        IReadOnlyList<NotificationOutboxItem> items,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO "T_InAppNotification"
                ("UserId", "Type", "Title", "Body", "IsRead", "CreatedAt", "SourceOutboxId")
            SELECT x."UserId", x."Type", x."Title", x."Body", FALSE, @created_at, x."SourceOutboxId"
            FROM unnest(@user_ids, @types, @titles, @bodies, @source_ids)
                AS x("UserId", "Type", "Title", "Body", "SourceOutboxId")
            ON CONFLICT ("SourceOutboxId") WHERE "SourceOutboxId" IS NOT NULL DO NOTHING
            """;

        object[] parameters =
        [
            new NpgsqlParameter("user_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
                { Value = items.Select(x => x.UserId).ToArray() },
            new NpgsqlParameter("types", NpgsqlDbType.Array | NpgsqlDbType.Text)
                { Value = items.Select(x => x.Type).ToArray() },
            new NpgsqlParameter("titles", NpgsqlDbType.Array | NpgsqlDbType.Text)
                { Value = items.Select(x => x.Title).ToArray() },
            new NpgsqlParameter("bodies", NpgsqlDbType.Array | NpgsqlDbType.Text)
                { Value = items.Select(x => x.Body).ToArray() },
            new NpgsqlParameter("source_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
                { Value = items.Select(x => x.Id).ToArray() },
            new NpgsqlParameter("created_at", NpgsqlDbType.TimestampTz)
                { Value = DateTimeOffset.UtcNow },
        ];

        await db.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
    }

    private Task<int> ReleaseLeaseAsync(
        NotificationOutboxItem item,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return db.NotificationOutbox
            .Where(x => x.Id == item.Id
                && x.Status == NotificationOutboxStatus.Processing
                && x.LockOwner == InstanceId
                && x.LockedAt == item.LockedAt)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status, NotificationOutboxStatus.Failed)
                    .SetProperty(x => x.LockedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.LockOwner, (string?)null)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);
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
        // 不预领超过当前可处理并发的任务，避免排队项在内存中耗尽数据库租约。
        var batchSize = Math.Min(Math.Max(1, opts.BatchSize), concurrency);
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
