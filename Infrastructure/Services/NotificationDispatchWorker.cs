using Core.Interfaces;
using Core.Models.Notifications;
using Core.Models.Export;
using Core.Models.Security;
using Core.Settings;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
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
    private const int DefaultMaxAttempts = 8;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public int MaxAttempts => DefaultMaxAttempts;

    public TimeSpan ProcessingLease => Lease;

    public async Task<int> ReclaimExpiredLeasesAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - Lease;
        var now = DateTimeOffset.UtcNow;
        var dead = await db.NotificationOutbox
            .Where(x => x.Status == NotificationOutboxStatus.Processing
                        && x.LockedAt != null
                        && x.LockedAt < cutoff
                        && x.AttemptCount + 1 >= DefaultMaxAttempts)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status, NotificationOutboxStatus.Dead)
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                    .SetProperty(x => x.LockedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.LockOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.LastError, "Processing lease expired; retry limit reached"),
                cancellationToken);
        var reclaimed = await db.NotificationOutbox
            .Where(x => x.Status == NotificationOutboxStatus.Processing
                        && x.LockedAt != null
                        && x.LockedAt < cutoff)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status, NotificationOutboxStatus.Failed)
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                    .SetProperty(x => x.LockedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.LockOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.LastError, "Processing lease expired"),
                cancellationToken);
        if (dead > 0)
            metrics.RecordDead();
        if (reclaimed > 0)
            metrics.RecordFailed();
        return dead + reclaimed;
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

    /// <summary>返回当前积压中最老任务的创建时间（oldest-job-age 指标来源）。无积压返回 null。</summary>
    public async Task<DateTimeOffset?> GetOldestPendingJobCreatedAtAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.NotificationOutbox.AsNoTracking()
            .Where(x => (x.Status == NotificationOutboxStatus.Pending || x.Status == NotificationOutboxStatus.Failed)
                        && x.NextAttemptAt <= now)
            .MinAsync(x => (DateTimeOffset?)x.CreatedAt, cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationOutboxItem>> ClaimDueItemsAsync(
        int batchSize, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        // P0-4：每次领取生成唯一 LeaseToken 作为 fencing token，替代 LockedAt 精度匹配。
        List<long> ids;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            ids = await db.Database
                .SqlQuery<long>($"""
                    UPDATE "T_NotificationOutbox" AS o
                    SET "Status" = {(int)NotificationOutboxStatus.Processing},
                        "LockedAt" = {now},
                        "LockOwner" = {InstanceId},
                        "LeaseToken" = md5(random()::text || clock_timestamp()::text || o."Id"::text),
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
        return await db.NotificationOutbox.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
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
        // 每个作业拥有独立 LeaseToken；批量插入仍然保留，但 fenced 更新必须逐条匹配
        // 自己的 token，避免某一条租约失效时误更新同批其他通知。
        foreach (var item in pending)
        {
            var updated = await db.NotificationOutbox
                .Where(x => x.Id == item.Id
                    && x.InAppDeliveredAt == null
                    && x.Status == NotificationOutboxStatus.Processing
                    && x.LockOwner == InstanceId
                    && x.LeaseToken == item.LeaseToken)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.InAppDeliveredAt, now)
                        .SetProperty(x => x.UpdatedAt, now),
                    cancellationToken);
            if (updated != 1)
                throw new InvalidOperationException($"通知 Outbox 租约已失效 Id={item.Id}");

            item.InAppDeliveredAt = now;
        }
    }

    /// <summary>
    /// Shared leased-job executor hook. Only idempotent external work and
    /// intermediate delivery markers run here; the final Sent update is
    /// performed by <see cref="CompleteClaimedAsync"/>.
    /// </summary>
    public async Task ExecuteClaimedAsync(
        NotificationOutboxItem item,
        CancellationToken cancellationToken = default)
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
                    && x.LeaseToken == item.LeaseToken)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.InAppDeliveredAt, deliveredAt)
                        .SetProperty(x => x.UpdatedAt, deliveredAt),
                    cancellationToken);
            if (updated != 1)
                throw new InvalidOperationException("通知 Outbox 站内投递租约已失效");
            item.InAppDeliveredAt = deliveredAt;
        }

        if (!item.PreferEmail || item.EmailDeliveredAt is not null)
            return;

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == item.UserId)
            .Select(u => new { u.NotifySecurityEmail, u.Email })
            .SingleOrDefaultAsync(cancellationToken);
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

        var emailDeliveredAt = DateTimeOffset.UtcNow;
        var emailUpdated = await db.NotificationOutbox
            .Where(x => x.Id == item.Id
                && x.EmailDeliveredAt == null
                && x.Status == NotificationOutboxStatus.Processing
                && x.LockOwner == InstanceId
                && x.LeaseToken == item.LeaseToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.EmailDeliveredAt, emailDeliveredAt)
                    .SetProperty(x => x.UpdatedAt, emailDeliveredAt),
                cancellationToken);
        if (emailUpdated != 1)
            throw new InvalidOperationException("通知 Outbox 邮件投递租约已失效");
        item.EmailDeliveredAt = emailDeliveredAt;
    }

    public async Task<LeaseRenewalResult> RenewAsync(
        NotificationOutboxItem item,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var until = now.Add(Lease);
        var updated = await db.NotificationOutbox
            .Where(x => x.Id == item.Id
                && x.Status == NotificationOutboxStatus.Processing
                && x.LockOwner == InstanceId
                && x.LeaseToken == item.LeaseToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.LockedAt, until)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);
        return updated == 1 ? LeaseRenewalResult.Renewed : LeaseRenewalResult.LeaseLost;
    }

    public async Task<bool> CompleteClaimedAsync(
        NotificationOutboxItem item,
        CancellationToken cancellationToken = default)
    {
        var updated = await db.NotificationOutbox
            .Where(x => x.Id == item.Id
                && x.Status == NotificationOutboxStatus.Processing
                && x.LockOwner == InstanceId
                && x.LeaseToken == item.LeaseToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status, NotificationOutboxStatus.Sent)
                    .SetProperty(x => x.LockedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.LockOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow)
                    .SetProperty(x => x.LastError, (string?)null),
                cancellationToken);
        if (updated == 1)
            metrics.RecordSent();
        return updated == 1;
    }

    public Task<bool> RetryClaimedAsync(
        NotificationOutboxItem item,
        string error,
        CancellationToken cancellationToken = default)
        => FinalizeFailureAsync(item, error, forceDeadLetter: false, cancellationToken);

    public Task<bool> DeadLetterClaimedAsync(
        NotificationOutboxItem item,
        string error,
        CancellationToken cancellationToken = default)
        => FinalizeFailureAsync(item, error, forceDeadLetter: true, cancellationToken);

    private async Task<bool> FinalizeFailureAsync(
        NotificationOutboxItem item,
        string error,
        bool forceDeadLetter,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var attempts = Math.Max(1, item.AttemptCount + 1);
        var dead = forceDeadLetter || attempts >= DefaultMaxAttempts;
        var message = error.Length <= 1000 ? error : error[..1000];
        var nextAttempt = dead
            ? now
            : now.Add(LeasedJobBackoff.ExponentialWithJitter(
                TimeSpan.FromSeconds(5), attempts, TimeSpan.FromHours(1)));
        var updated = await db.NotificationOutbox
            .Where(x => x.Id == item.Id
                && x.Status == NotificationOutboxStatus.Processing
                && x.LockOwner == InstanceId
                && x.LeaseToken == item.LeaseToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status,
                        dead ? NotificationOutboxStatus.Dead : NotificationOutboxStatus.Failed)
                    .SetProperty(x => x.AttemptCount, attempts)
                    .SetProperty(x => x.LastError, message)
                    .SetProperty(x => x.NextAttemptAt, nextAttempt)
                    .SetProperty(x => x.LockedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.LockOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);
        if (updated == 1)
        {
            if (dead) metrics.RecordDead();
            else metrics.RecordFailed();
        }
        return updated == 1;
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
                        && x.LeaseToken == item.LeaseToken)
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
                        && x.LeaseToken == item.LeaseToken)
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
                        && x.LeaseToken == item.LeaseToken)
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
                    && x.LeaseToken == item.LeaseToken)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.Status, NotificationOutboxStatus.Sent)
                        .SetProperty(x => x.LockedAt, (DateTimeOffset?)null)
                        .SetProperty(x => x.LockOwner, (string?)null)
                        .SetProperty(x => x.LeaseToken, (string?)null)
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
            var dead = attempts >= DefaultMaxAttempts;
            var delay = LeasedJobBackoff.ExponentialWithJitter(
                TimeSpan.FromSeconds(5), attempts, TimeSpan.FromHours(1));
            await db.NotificationOutbox
                .Where(x => x.Id == item.Id
                    && x.Status == NotificationOutboxStatus.Processing
                    && x.LockOwner == InstanceId
                    && x.LeaseToken == item.LeaseToken)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.Status, dead ? NotificationOutboxStatus.Dead : NotificationOutboxStatus.Failed)
                        .SetProperty(x => x.AttemptCount, attempts)
                        .SetProperty(x => x.LastError, ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message)
                        .SetProperty(x => x.LockedAt, (DateTimeOffset?)null)
                        .SetProperty(x => x.LockOwner, (string?)null)
                        .SetProperty(x => x.LeaseToken, (string?)null)
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
                && x.LeaseToken == item.LeaseToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status, NotificationOutboxStatus.Failed)
                    .SetProperty(x => x.LockedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.LockOwner, (string?)null)
                        .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);
    }
}

public sealed class NotificationDispatchWorker(
    NotificationOutboxJobStore jobStore,
    LeasedJobExecutor<NotificationOutboxItem> executor,
    IOptions<NotificationOutboxOptions> options,
    IOptions<WorkerConcurrencyOptions> workerConcurrencyOptions,
    WorkerConcurrencyManager concurrencyManager,
    NotificationOutboxMetrics metrics,
    ILogger<NotificationDispatchWorker> logger) : BackgroundService
{
    private const string WorkerName = "notification";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds));
        var backlogEvery = TimeSpan.FromSeconds(Math.Max(5, options.Value.BacklogSampleSeconds));
        var nextBacklogSample = DateTimeOffset.MinValue;
        var workerConcurrency = Math.Max(1, workerConcurrencyOptions.Value.NotificationDispatch);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextBacklogSample)
                {
                    metrics.SetBacklog(await jobStore.CountBacklogAsync(stoppingToken)
                        .ConfigureAwait(false));
                    concurrencyManager.RecordOldestPendingJob(
                        WorkerName,
                        await jobStore.GetOldestPendingJobCreatedAtAsync(stoppingToken)
                            .ConfigureAwait(false));
                    nextBacklogSample = DateTimeOffset.UtcNow + backlogEvery;
                }

                var completed = await executor.DrainAsync(
                        WorkerName,
                        workerConcurrency,
                        jobStore.ProcessingLease,
                        jobStore,
                        jobStore.ExecuteClaimedAsync,
                        job => job.AttemptCount + 1 >= jobStore.MaxAttempts,
                        stoppingToken)
                    .ConfigureAwait(false);
                if (completed == 0)
                    await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "通知 Outbox 轮询异常");
                await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
