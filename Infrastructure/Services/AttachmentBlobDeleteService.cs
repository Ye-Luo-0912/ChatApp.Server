using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public sealed class AttachmentBlobDeleteService(
    UserDbContext db,
    IAttachmentStorage storage,
    IOptions<AttachmentStorageOptions> options,
    ILogger<AttachmentBlobDeleteService> logger) : IAttachmentBlobDeleteService
{
    private static readonly string ProcessOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}";
    private static readonly string[] ActiveStatuses =
    [
        AttachmentBlobDeleteJobStatus.Pending,
        AttachmentBlobDeleteJobStatus.Processing,
    ];

    /// <summary>删除通常是短 I/O；实例崩溃后可安全接管。</summary>
    public const int LeaseMinutes = 5;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(LeaseMinutes);

    public Task EnqueueAsync(
        IEnumerable<string> objectKeys,
        long? userId = null,
        string? attachmentId = null,
        CancellationToken cancellationToken = default)
        => EnqueueAsync(
            objectKeys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => (k.Trim(), attachmentId)),
            userId,
            cancellationToken);

    public async Task EnqueueAsync(
        IEnumerable<(string ObjectKey, string? AttachmentId)> items,
        long? userId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (objectKey, attachmentId) in items)
        {
            if (!string.IsNullOrWhiteSpace(objectKey))
                normalized.TryAdd(objectKey.Trim(), attachmentId);
        }

        if (normalized.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var objectKeys = normalized.Keys.ToArray();
        var activeKeys = await db.AttachmentBlobDeleteJobs
            .AsNoTracking()
            .Where(j => objectKeys.Contains(j.ObjectKey) && ActiveStatuses.Contains(j.Status))
            .Select(j => j.ObjectKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var active = activeKeys.ToHashSet(StringComparer.Ordinal);
        var pending = normalized
            .Where(pair => !active.Contains(pair.Key))
            .Select(pair => (pair.Key, pair.Value))
            .ToArray();
        if (pending.Length == 0)
            return;

        var queued = IsNpgsql()
            ? await EnqueueNpgsqlAsync(pending, userId, now, cancellationToken).ConfigureAwait(false)
            : await EnqueueFallbackAsync(pending, userId, now, cancellationToken).ConfigureAwait(false);
        if (queued == 0)
            return;

        AuthSecurityMetrics.AttachmentPendingDeleteDelta(queued);
        logger.LogInformation(
            "已入队 {Count} 条附件 blob 删除墓碑 UserId={UserId}",
            queued,
            userId);
    }

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var batchSize = Math.Clamp(options.Value.DeleteBatchSize, 1, 500);
        var claimed = await ClaimDueJobsAsync(batchSize, cancellationToken).ConfigureAwait(false);
        var deleted = 0;
        foreach (var job in claimed)
        {
            if (await ProcessClaimedJobAsync(job, cancellationToken).ConfigureAwait(false))
                deleted++;
        }

        return deleted;
    }

    /// <summary>
    /// 原子领取到期或租约过期的墓碑。生产 PostgreSQL 使用 <c>FOR UPDATE SKIP LOCKED</c>，
    /// 每个领取批次都有独立 owner/token，旧 Worker 无法覆盖新的持有者。
    /// </summary>
    public async Task<IReadOnlyList<AttachmentBlobDeleteJob>> ClaimDueJobsAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(Lease);
        var owner = $"{ProcessOwner}:{Guid.NewGuid():N}";
        if (owner.Length > 128)
            owner = owner[..128];
        var leaseToken = Guid.NewGuid().ToString("N");

        if (IsNpgsql())
        {
            var claimedIds = await ClaimDueJobIdsNpgsqlAsync(
                    batchSize, owner, leaseToken, now, leaseUntil, cancellationToken)
                .ConfigureAwait(false);
            if (claimedIds.Count == 0)
                return [];

            return await db.AttachmentBlobDeleteJobs
                .AsNoTracking()
                .Where(j => claimedIds.Contains(j.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        // InMemory/SQLite 测试回退：生产路径不依赖此实现。
        var due = await db.AttachmentBlobDeleteJobs
            .Where(j =>
                (j.Status == AttachmentBlobDeleteJobStatus.Pending && j.NextAttemptAt <= now)
                || (j.Status == AttachmentBlobDeleteJobStatus.Processing
                    && j.LeaseExpiresAt != null
                    && j.LeaseExpiresAt < now))
            .OrderBy(j => j.NextAttemptAt)
            .ThenBy(j => j.Id)
            .Take(Math.Clamp(batchSize, 1, 500))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (due.Count == 0)
            return due;

        foreach (var job in due)
        {
            job.Status = AttachmentBlobDeleteJobStatus.Processing;
            job.LeaseOwner = owner;
            job.LeaseToken = leaseToken;
            job.LeaseExpiresAt = leaseUntil;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return due;
    }

    /// <summary>
    /// 外部删除完成后仅由仍持有同一租约的实例更新数据库。重试耗尽后进入
    /// DeadLetter，而不是继续保留 Pending 并无限重试。
    /// </summary>
    public async Task<bool> ProcessClaimedJobAsync(
        AttachmentBlobDeleteJob claimed,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(claimed.LeaseOwner)
            || string.IsNullOrWhiteSpace(claimed.LeaseToken))
            return false;

        var opts = options.Value;
        var maxAttempts = Math.Max(1, opts.MaxDeleteAttempts);
        try
        {
            await storage.DeleteAsync(claimed.ObjectKey, cancellationToken).ConfigureAwait(false);
            var completed = await ApplyFencedUpdateAsync(
                    claimed,
                    new TargetFields
                    {
                        Status = AttachmentBlobDeleteJobStatus.Done,
                        AttemptCount = claimed.AttemptCount,
                        CompletedAt = DateTimeOffset.UtcNow,
                        LastError = null,
                        NextAttemptAt = claimed.NextAttemptAt,
                        LeaseOwner = null,
                        LeaseToken = null,
                        LeaseExpiresAt = null,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!completed)
            {
                logger.LogInformation(
                    "附件 blob 删除已完成但租约已易主，丢弃旧结果 JobId={Id} Key={Key}",
                    claimed.Id,
                    claimed.ObjectKey);
                return false;
            }

            AuthSecurityMetrics.AttachmentBlobDelete("success");
            AuthSecurityMetrics.AttachmentPendingDeleteDelta(-1);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var attemptCount = Math.Max(1, claimed.AttemptCount + 1);
            var exhausted = attemptCount >= maxAttempts;
            var updated = await ApplyFencedUpdateAsync(
                    claimed,
                    new TargetFields
                    {
                        Status = exhausted
                            ? AttachmentBlobDeleteJobStatus.DeadLetter
                            : AttachmentBlobDeleteJobStatus.Pending,
                        AttemptCount = attemptCount,
                        CompletedAt = exhausted ? DateTimeOffset.UtcNow : null,
                        LastError = Truncate(ex.Message, 500),
                        NextAttemptAt = exhausted
                            ? DateTimeOffset.UtcNow
                            : DateTimeOffset.UtcNow.Add(ComputeBackoff(opts, attemptCount)),
                        LeaseOwner = null,
                        LeaseToken = null,
                        LeaseExpiresAt = null,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!updated)
                return false;

            AuthSecurityMetrics.AttachmentBlobDelete("failed");
            if (exhausted)
            {
                AuthSecurityMetrics.AttachmentBlobDelete("exhausted");
                AuthSecurityMetrics.AttachmentPendingDeleteDelta(-1);
                logger.LogError(
                    ex,
                    "附件 blob 删除重试已耗尽，转入 DeadLetter JobId={Id} Key={Key} Attempts={Attempt}",
                    claimed.Id,
                    claimed.ObjectKey,
                    attemptCount);
            }
            else
            {
                logger.LogWarning(
                    ex,
                    "附件 blob 删除失败，已按退避重新入队 JobId={Id} Key={Key} Attempt={Attempt}",
                    claimed.Id,
                    claimed.ObjectKey,
                    attemptCount);
            }

            return false;
        }
    }

    private async Task<int> EnqueueFallbackAsync(
        IReadOnlyList<(string ObjectKey, string? AttachmentId)> items,
        long? userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        db.AttachmentBlobDeleteJobs.AddRange(items.Select(item => new AttachmentBlobDeleteJob
        {
            ObjectKey = item.ObjectKey,
            AttachmentId = item.AttachmentId,
            UserId = userId,
            Status = AttachmentBlobDeleteJobStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = now,
            CreatedAt = now,
        }));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return items.Count;
    }

    private async Task<int> EnqueueNpgsqlAsync(
        IReadOnlyList<(string ObjectKey, string? AttachmentId)> items,
        long? userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();
        command.CommandText =
            """
            INSERT INTO "T_AttachmentBlobDeleteJob"
                ("ObjectKey", "AttachmentId", "UserId", "Status", "AttemptCount", "NextAttemptAt", "CreatedAt")
            SELECT i."ObjectKey", i."AttachmentId", @user_id::bigint, 'Pending', 0, @now, @now
            FROM unnest(@object_keys::text[], @attachment_ids::text[])
                AS i("ObjectKey", "AttachmentId")
            ON CONFLICT DO NOTHING
            RETURNING "Id";
            """;
        AddParameter(command, "user_id", userId.HasValue ? (object)userId.Value : DBNull.Value);
        AddParameter(command, "now", now);
        AddParameter(command, "object_keys", items.Select(item => item.ObjectKey).ToArray());
        AddParameter(command, "attachment_ids", items.Select(item => item.AttachmentId).ToArray());

        var queued = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            queued++;
        return queued;
    }

    private async Task<List<long>> ClaimDueJobIdsNpgsqlAsync(
        int batchSize,
        string owner,
        string leaseToken,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();
        command.CommandText =
            """
            UPDATE "T_AttachmentBlobDeleteJob" AS j
            SET "Status" = 'Processing',
                "LeaseOwner" = @owner,
                "LeaseToken" = @lease_token,
                "LeaseExpiresAt" = @lease_until
            WHERE j."Id" IN (
                SELECT c."Id"
                FROM "T_AttachmentBlobDeleteJob" AS c
                WHERE (c."Status" = 'Pending' AND c."NextAttemptAt" <= @now)
                   OR (c."Status" = 'Processing'
                       AND c."LeaseExpiresAt" IS NOT NULL
                       AND c."LeaseExpiresAt" < @now)
                ORDER BY c."NextAttemptAt", c."Id"
                FOR UPDATE SKIP LOCKED
                LIMIT @batch
            )
            RETURNING j."Id";
            """;
        AddParameter(command, "owner", owner);
        AddParameter(command, "lease_token", leaseToken);
        AddParameter(command, "lease_until", leaseUntil);
        AddParameter(command, "now", now);
        AddParameter(command, "batch", Math.Clamp(batchSize, 1, 500));

        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private bool IsNpgsql() =>
        db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    private sealed class TargetFields
    {
        public string Status { get; init; } = string.Empty;
        public int AttemptCount { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public string? LastError { get; init; }
        public DateTimeOffset NextAttemptAt { get; init; }
        public string? LeaseOwner { get; init; }
        public string? LeaseToken { get; init; }
        public DateTimeOffset? LeaseExpiresAt { get; init; }
    }

    private async Task<bool> ApplyFencedUpdateAsync(
        AttachmentBlobDeleteJob claimed,
        TargetFields target,
        CancellationToken cancellationToken)
    {
        if (!IsNpgsql())
        {
            var tracked = await db.AttachmentBlobDeleteJobs
                .FirstOrDefaultAsync(j => j.Id == claimed.Id, cancellationToken)
                .ConfigureAwait(false);
            if (tracked is null
                || tracked.Status != AttachmentBlobDeleteJobStatus.Processing
                || tracked.LeaseOwner != claimed.LeaseOwner
                || tracked.LeaseToken != claimed.LeaseToken)
                return false;

            tracked.Status = target.Status;
            tracked.AttemptCount = target.AttemptCount;
            tracked.CompletedAt = target.CompletedAt;
            tracked.LastError = target.LastError;
            tracked.NextAttemptAt = target.NextAttemptAt;
            tracked.LeaseOwner = target.LeaseOwner;
            tracked.LeaseToken = target.LeaseToken;
            tracked.LeaseExpiresAt = target.LeaseExpiresAt;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var updated = await db.AttachmentBlobDeleteJobs
            .Where(j => j.Id == claimed.Id
                && j.Status == AttachmentBlobDeleteJobStatus.Processing
                && j.LeaseOwner == claimed.LeaseOwner
                && j.LeaseToken == claimed.LeaseToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, target.Status)
                    .SetProperty(j => j.AttemptCount, target.AttemptCount)
                    .SetProperty(j => j.CompletedAt, target.CompletedAt)
                    .SetProperty(j => j.LastError, target.LastError)
                    .SetProperty(j => j.NextAttemptAt, target.NextAttemptAt)
                    .SetProperty(j => j.LeaseOwner, target.LeaseOwner)
                    .SetProperty(j => j.LeaseToken, target.LeaseToken)
                    .SetProperty(j => j.LeaseExpiresAt, target.LeaseExpiresAt),
                cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    private static TimeSpan ComputeBackoff(AttachmentStorageOptions opts, int attemptCount)
    {
        var baseSeconds = Math.Max(5, opts.DeleteBackoffSeconds);
        var exp = Math.Min(attemptCount - 1, 10);
        var seconds = Math.Min(3600, baseSeconds * Math.Pow(2, Math.Max(0, exp)));
        return TimeSpan.FromSeconds(seconds);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}

/// <summary>DI 作用域工厂封装，供 BackgroundService 入队。</summary>
public sealed class AttachmentBlobDeleteEnqueuer(IServiceScopeFactory scopeFactory) 
{
    public async Task EnqueueAsync(
        IEnumerable<string> objectKeys,
        long? userId = null,
        string? attachmentId = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAttachmentBlobDeleteService>();
        await svc.EnqueueAsync(objectKeys, userId, attachmentId, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnqueueAsync(
        IEnumerable<(string ObjectKey, string? AttachmentId)> items,
        long? userId = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAttachmentBlobDeleteService>();
        await svc.EnqueueAsync(items, userId, cancellationToken).ConfigureAwait(false);
    }
}
