using Core.Models.Email;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Email;

/// <summary>
/// Outbox 领取 / 租约回收 / 发送处理；供 Worker 与集成测试共用。
/// </summary>
public sealed class EmailOutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    Func<string, string, string, bool, CancellationToken, Task<EmailResult>> sendEmail,
    EmailOutboxMetrics metrics,
    ILogger logger,
    string? ownerId = null,
    TimeSpan? processingLease = null,
    int maxAttempts = 5,
    int batchSize = 20)
{
    public static readonly TimeSpan DefaultProcessingLease = TimeSpan.FromMinutes(5);

    private readonly string _ownerId = ownerId
        ?? $"{Environment.MachineName}:{Guid.NewGuid():N}"[..Math.Min(128, Environment.MachineName.Length + 33)];
    private readonly TimeSpan _processingLease = processingLease ?? DefaultProcessingLease;
    private readonly int _maxAttempts = maxAttempts;
    private readonly int _batchSize = batchSize;

    public string OwnerId => _ownerId;

    public async Task<int> ReclaimStaleProcessingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var cutoff = DateTime.UtcNow - _processingLease;

        var reclaimed = await db.EmailOutbox
            .Where(x => x.Status == EmailOutboxStatus.Processing
                        && x.LockedAt != null
                        && x.LockedAt < cutoff)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, EmailOutboxStatus.Failed)
                .SetProperty(x => x.NextAttemptAt, DateTime.UtcNow)
                .SetProperty(x => x.LockedAt, (DateTime?)null)
                .SetProperty(x => x.LockOwner, (string?)null)
                .SetProperty(x => x.LastError, "Processing lease expired")
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        if (reclaimed > 0)
            logger.LogWarning("回收超时 Processing 邮件 {Count} 条", reclaimed);

        return reclaimed;
    }

    public async Task<int> ArchiveSentAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var cutoff = DateTime.UtcNow - retention;

        return await db.EmailOutbox
            .Where(x => x.Status == EmailOutboxStatus.Sent && x.UpdatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EmailOutboxItem>> ClaimDueItemsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTime.UtcNow;

        List<long> dueIds;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            dueIds = await db.Database
                .SqlQuery<long>($"""
                    UPDATE "T_EmailOutbox" AS o
                    SET "Status" = {(int)EmailOutboxStatus.Processing},
                        "LockedAt" = {now},
                        "LockOwner" = {_ownerId},
                        "UpdatedAt" = {now}
                    WHERE o."Id" IN (
                        SELECT i."Id" FROM "T_EmailOutbox" AS i
                        WHERE i."Status" IN ({(int)EmailOutboxStatus.Pending}, {(int)EmailOutboxStatus.Failed})
                          AND i."NextAttemptAt" <= {now}
                        ORDER BY i."NextAttemptAt"
                        FOR UPDATE SKIP LOCKED
                        LIMIT {_batchSize}
                    )
                    RETURNING o."Id"
                    """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (dueIds.Count == 0)
            return [];

        return await db.EmailOutbox
            .AsNoTracking()
            .Where(x => dueIds.Contains(x.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 处理单条任务。返回是否发送成功。
    /// <para>关闭取消时退回租约且不增加 AttemptCount。</para>
    /// </summary>
    public async Task ProcessItemAsync(EmailOutboxItem item, CancellationToken cancellationToken)
    {
        try
        {
            var sendResult = await sendEmail(item.To, item.Subject, item.Body, item.IsHtml, cancellationToken)
                .ConfigureAwait(false);

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var now = DateTime.UtcNow;

            if (sendResult.IsSuccess)
            {
                await db.EmailOutbox
                    .Where(x => x.Id == item.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, EmailOutboxStatus.Sent)
                        .SetProperty(x => x.LockedAt, (DateTime?)null)
                        .SetProperty(x => x.LockOwner, (string?)null)
                        .SetProperty(x => x.UpdatedAt, now), cancellationToken)
                    .ConfigureAwait(false);

                metrics.RecordSent();
                return;
            }

            await HandleFailureAsync(db, item, sendResult.ErrorMessage ?? "邮件发送失败", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseLeaseWithoutFailureAsync(item.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "邮件 Outbox 处理异常 Id={OutboxId} To={To}", item.Id, item.To);

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await HandleFailureAsync(db, item, ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>测试钩子：SMTP 已成功但落库前崩溃 → 条目仍为 Processing，租约回收后可重发。</summary>
    public async Task SimulateCrashAfterSendBeforePersistAsync(EmailOutboxItem item, CancellationToken cancellationToken)
    {
        var sendResult = await sendEmail(item.To, item.Subject, item.Body, item.IsHtml, cancellationToken)
            .ConfigureAwait(false);
        if (!sendResult.IsSuccess)
            throw new InvalidOperationException(sendResult.ErrorMessage ?? "send failed");
        // 故意不更新数据库，保留 Processing
    }

    public async Task RetryDeadLetterAsync(long id, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTime.UtcNow;

        await db.EmailOutbox
            .Where(x => x.Id == id && (x.Status == EmailOutboxStatus.Dead || x.Status == EmailOutboxStatus.Failed))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, EmailOutboxStatus.Pending)
                .SetProperty(x => x.NextAttemptAt, now)
                .SetProperty(x => x.LockedAt, (DateTime?)null)
                .SetProperty(x => x.LockOwner, (string?)null)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.LastError, (string?)null), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ReleaseLeaseWithoutFailureAsync(long id, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var now = DateTime.UtcNow;

            await db.EmailOutbox
                .Where(x => x.Id == id && x.Status == EmailOutboxStatus.Processing)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, EmailOutboxStatus.Pending)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.LockedAt, (DateTime?)null)
                    .SetProperty(x => x.LockOwner, (string?)null)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "取消时退回 Outbox 租约失败 Id={OutboxId}", id);
        }
    }

    private async Task HandleFailureAsync(
        UserDbContext db,
        EmailOutboxItem item,
        string error,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var attemptCount = item.AttemptCount + 1;
        var truncatedError = error.Length <= 2048 ? error : error[..2048];

        if (attemptCount >= _maxAttempts)
        {
            await db.EmailOutbox
                .Where(x => x.Id == item.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, EmailOutboxStatus.Dead)
                    .SetProperty(x => x.AttemptCount, attemptCount)
                    .SetProperty(x => x.LastError, truncatedError)
                    .SetProperty(x => x.LockedAt, (DateTime?)null)
                    .SetProperty(x => x.LockOwner, (string?)null)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false);

            metrics.RecordDead();
            return;
        }

        var nextAttemptAt = now.Add(CalculateBackoff(attemptCount));

        await db.EmailOutbox
            .Where(x => x.Id == item.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, EmailOutboxStatus.Failed)
                .SetProperty(x => x.AttemptCount, attemptCount)
                .SetProperty(x => x.LastError, truncatedError)
                .SetProperty(x => x.NextAttemptAt, nextAttemptAt)
                .SetProperty(x => x.LockedAt, (DateTime?)null)
                .SetProperty(x => x.LockOwner, (string?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);

        metrics.RecordFailed();
    }

    private static TimeSpan CalculateBackoff(int attemptCount)
    {
        var baseSeconds = Math.Min(3600, Math.Pow(2, attemptCount - 1) * 30);
        var jitter = 1.0 + (Random.Shared.NextDouble() * 0.4 - 0.2);
        return TimeSpan.FromSeconds(baseSeconds * jitter);
    }
}
