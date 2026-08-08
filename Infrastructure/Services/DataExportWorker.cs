using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Infrastructure.Serialization;

namespace Infrastructure.Services;

/// <summary>
/// Data-export worker using the common reservation/claim/heartbeat/fenced
/// execution kernel. Cleanup remains a separate short-lived maintenance pass.
/// </summary>
public sealed class DataExportWorker(
    IServiceScopeFactory scopeFactory,
    DataExportJobStore jobStore,
    LeasedJobExecutor<DataExportJob> executor,
    IOptions<DataExportStorageOptions> options,
    IOptions<WorkerConcurrencyOptions> workerConcurrencyOptions,
    ILogger<DataExportWorker> logger) : BackgroundService
{
    private const string WorkerName = "data_export";

    // Compatibility constructor for focused integration tests and older hosts
    // that constructed the worker before the shared job store was introduced.
    [ActivatorUtilitiesConstructor]
    public DataExportWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<DataExportStorageOptions> options,
        IOptions<WorkerConcurrencyOptions> workerConcurrencyOptions,
        WorkerConcurrencyManager concurrencyManager,
        ILogger<DataExportWorker> logger)
        : this(
            scopeFactory,
            new DataExportJobStore(scopeFactory, options),
            new LeasedJobExecutor<DataExportJob>(
                concurrencyManager,
                NullLogger<LeasedJobExecutor<DataExportJob>>.Instance),
            options,
            workerConcurrencyOptions,
            logger)
    {
    }

    internal static Task<(DataExportJob Job, string LeaseToken)?> ClaimOneNpgsqlAsync(
        UserDbContext db,
        string owner,
        string leaseToken,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken)
        => DataExportJobProcessor.ClaimOneNpgsqlAsync(
            db, owner, leaseToken, now, leaseUntil, cancellationToken);

    internal static Task WriteChatExportAsync(
        SequentialJsonObjectWriter writer,
        IRealtimeChatExportReader chatExport,
        long userId,
        DataExportStorageOptions opts,
        CancellationToken cancellationToken)
        => DataExportJobProcessor.WriteChatExportAsync(
            writer, chatExport, userId, opts, cancellationToken);

    internal static Task WriteChatExportAsync(
        SequentialJsonObjectWriter writer,
        IRealtimeChatExportReader chatExport,
        IAttachmentMetadataStore attachmentMeta,
        long userId,
        DataExportStorageOptions opts,
        CancellationToken cancellationToken)
        => DataExportJobProcessor.WriteChatExportAsync(
            writer, chatExport, attachmentMeta, userId, opts, cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Max(500, options.Value.PollIntervalMilliseconds));
        var cleanupEvery = TimeSpan.FromMinutes(Math.Max(1, options.Value.CleanupIntervalMinutes));
        var nextCleanup = DateTimeOffset.MinValue;
        var workerConcurrency = Math.Max(1, workerConcurrencyOptions.Value.DataExport);

        try
        {
            CleanupStagingResidue();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "导出 Worker 启动时清理 staging 残留失败");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextCleanup)
                {
                    await CleanupExpiredAsync(stoppingToken).ConfigureAwait(false);
                    nextCleanup = DateTimeOffset.UtcNow + cleanupEvery;
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
                logger.LogError(ex, "导出 Worker 循环异常");
                await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task CleanupExpiredAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IDataExportBlobStore>();
        var now = DateTimeOffset.UtcNow;
        var maxDeleteAttempts = Math.Max(1, options.Value.MaxBlobDeleteAttempts);

        await RetryPendingDeletesAsync(db, blob, maxDeleteAttempts, cancellationToken)
            .ConfigureAwait(false);

        var expired = await db.DataExportJobs.AsNoTracking()
            .Where(j => (j.ExpiresAt != null && j.ExpiresAt < now
                         && (j.Status == DataExportJobStatus.Ready
                             || j.Status == DataExportJobStatus.Consumed))
                        || j.Status == DataExportJobStatus.Expired)
            .OrderBy(j => j.CreatedAt)
            .Take(100)
            .Select(j => new { j.Id, j.ObjectKey })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var job in expired)
        {
            if (string.IsNullOrWhiteSpace(job.ObjectKey))
            {
                await db.DataExportJobs.Where(j => j.Id == job.Id)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            await DataExportService.TryDeleteBlobOrTombstoneAsync(
                    db, blob, job.Id, job.ObjectKey, "cleanup")
                .ConfigureAwait(false);
        }

        await db.DataExportJobs
            .Where(j => j.Status == DataExportJobStatus.Ready
                        && j.ExpiresAt != null
                        && j.ExpiresAt < now
                        && j.ConsumedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, DataExportJobStatus.Expired),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void CleanupStagingResidue()
    {
        var root = DataExportJobProcessor.GetStagingRoot(options.Value);
        var cutoff = DateTime.UtcNow.AddSeconds(-Math.Max(60, options.Value.LeaseSeconds * 2));
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (File.GetLastWriteTimeUtc(path) >= cutoff)
                continue;

            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "清理导出 staging 崩溃残留失败 Path={Path}", path);
            }
        }

        DataExportJobProcessor.RefreshStagingBytes(root);
        if (deleted > 0)
            logger.LogInformation("已清理 {Count} 个导出 staging 崩溃残留", deleted);
    }

    private async Task RetryPendingDeletesAsync(
        UserDbContext db,
        IDataExportBlobStore blob,
        int maxDeleteAttempts,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.AddSeconds(Math.Max(30, options.Value.LeaseSeconds));
        var owner = $"export-delete:{Environment.MachineName}:{Guid.NewGuid():N}";
        IReadOnlyList<string> ids;

        // AttemptCount is the durable count of cleanup claims. A process can
        // die after acquiring the cleanup lease and before the blob call, so
        // only incrementing it after a delete failure would allow the same
        // row to be reclaimed forever without ever reaching DLQ.
        var exhausted = await db.DataExportJobs
            .Where(j => j.ObjectKey != null
                        && (j.Status == DataExportJobStatus.PendingDelete
                            || j.Status == DataExportJobStatus.ConsumedPendingDelete)
                        && j.AttemptCount >= maxDeleteAttempts
                        && (j.DownloadLeaseUntil == null || j.DownloadLeaseUntil <= now)
                        && (j.LeaseUntil == null || j.LeaseUntil < now))
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, DataExportJobStatus.DeleteDeadLetter)
                    .SetProperty(j => j.Error, "blob_delete_reclaim_limit_reached")
                    .SetProperty(j => j.LeaseOwner, (string?)null)
                    .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(j => j.LeaseToken, (string?)null),
                cancellationToken)
            .ConfigureAwait(false);
        if (exhausted > 0)
        {
            AuthSecurityMetrics.ExportBlobDelete("reclaim_dead_letter");
            logger.LogError(
                "导出 blob 删除因租约回收次数耗尽进入死信 Count={Count} MaxAttempts={MaxAttempts}",
                exhausted,
                maxDeleteAttempts);
        }

        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            ids = await db.Database.SqlQuery<string>($"""
                    UPDATE "T_DataExportJob" AS j
                    SET "LeaseOwner" = {owner},
                        "LeaseUntil" = {leaseUntil},
                        "LeaseToken" = md5({owner} || ':' || j."Id"),
                        "AttemptCount" = j."AttemptCount" + 1
                    WHERE j."Id" IN (
                        SELECT c."Id"
                        FROM "T_DataExportJob" AS c
                        WHERE c."ObjectKey" IS NOT NULL
                          AND c."Status" IN ('PendingDelete', 'ConsumedPendingDelete')
                          AND c."AttemptCount" < {maxDeleteAttempts}
                          AND c."NextAttemptAt" <= {now}
                          AND (c."DownloadLeaseUntil" IS NULL OR c."DownloadLeaseUntil" <= {now})
                          AND (c."LeaseUntil" IS NULL OR c."LeaseUntil" < {now})
                        ORDER BY c."CreatedAt", c."Id"
                        FOR UPDATE SKIP LOCKED
                        LIMIT 100
                    )
                    RETURNING j."Id"
                    """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var due = await db.DataExportJobs
                .Where(j => j.ObjectKey != null
                            && (j.Status == DataExportJobStatus.PendingDelete
                                || j.Status == DataExportJobStatus.ConsumedPendingDelete)
                            && j.AttemptCount < maxDeleteAttempts
                            && j.NextAttemptAt <= now
                            && (j.DownloadLeaseUntil == null || j.DownloadLeaseUntil <= now)
                            && (j.LeaseUntil == null || j.LeaseUntil < now))
                .OrderBy(j => j.CreatedAt)
                .Take(100)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var job in due)
            {
                job.LeaseOwner = owner;
                job.LeaseUntil = leaseUntil;
                job.LeaseToken = Guid.NewGuid().ToString("N");
                job.AttemptCount++;
            }
            if (due.Count > 0)
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            ids = due.Select(j => j.Id).ToArray();
        }

        if (ids.Count == 0)
            return;

        var claims = await db.DataExportJobs
            .AsNoTracking()
            .Where(j => ids.Contains(j.Id)
                        && j.LeaseOwner == owner
                        && j.LeaseUntil == leaseUntil
                        && j.LeaseToken != null)
            .Select(j => new DeleteClaim(
                j.Id,
                j.ObjectKey,
                j.Status,
                j.LeaseToken!,
                j.AttemptCount))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var claim in claims)
        {
            if (string.IsNullOrWhiteSpace(claim.ObjectKey))
                continue;

            var objectKey = claim.ObjectKey;
            try
            {
                await blob.DeleteAsync(objectKey, cancellationToken).ConfigureAwait(false);
                AuthSecurityMetrics.ExportBlobDelete("retry_success");
                var finalized = claim.Status == DataExportJobStatus.ConsumedPendingDelete
                    ? await db.DataExportJobs
                        .Where(j => j.Id == claim.Id
                            && j.Status == DataExportJobStatus.ConsumedPendingDelete
                            && j.LeaseOwner == owner
                            && j.LeaseUntil == leaseUntil
                            && j.LeaseToken == claim.LeaseToken)
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(j => j.Status, DataExportJobStatus.Consumed)
                                .SetProperty(j => j.ObjectKey, (string?)null)
                                .SetProperty(j => j.Error, (string?)null)
                                .SetProperty(j => j.DownloadLeaseUntil, (DateTimeOffset?)null)
                                .SetProperty(j => j.LeaseOwner, (string?)null)
                                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                                .SetProperty(j => j.LeaseToken, (string?)null),
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await db.DataExportJobs
                        .Where(j => j.Id == claim.Id
                            && j.Status == DataExportJobStatus.PendingDelete
                            && j.LeaseOwner == owner
                            && j.LeaseUntil == leaseUntil
                            && j.LeaseToken == claim.LeaseToken)
                        .ExecuteDeleteAsync(cancellationToken)
                        .ConfigureAwait(false);
                if (finalized == 1)
                    AuthSecurityMetrics.ExportPendingDeleteDelta(-1);
            }
            catch (Exception ex)
            {
                AuthSecurityMetrics.ExportBlobDelete("retry_failed");
                var message = ex.Message.Length <= 500 ? ex.Message : ex.Message[..500];
                // AttemptCount was incremented atomically when this cleanup
                // lease was claimed. Do not increment it again here: one
                // external delete attempt consumes exactly one attempt.
                var attempt = claim.AttemptCount;
                var deadLetter = attempt >= maxDeleteAttempts;
                var nextAttemptAt = DateTimeOffset.UtcNow.Add(
                    LeasedJobBackoff.ExponentialWithJitter(
                        TimeSpan.FromSeconds(5),
                        Math.Max(1, attempt),
                        TimeSpan.FromHours(6)));
                var updated = await db.DataExportJobs
                    .Where(j => j.Id == claim.Id
                        && j.LeaseOwner == owner
                        && j.LeaseUntil == leaseUntil
                        && j.LeaseToken == claim.LeaseToken
                        && (j.Status == DataExportJobStatus.PendingDelete
                            || j.Status == DataExportJobStatus.ConsumedPendingDelete))
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(j => j.Status,
                                deadLetter
                                    ? DataExportJobStatus.DeleteDeadLetter
                                    : claim.Status)
                            .SetProperty(j => j.AttemptCount, attempt)
                            .SetProperty(j => j.Error, message)
                            .SetProperty(j => j.NextAttemptAt, nextAttemptAt)
                            .SetProperty(j => j.LeaseOwner, (string?)null)
                            .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                            .SetProperty(j => j.LeaseToken, (string?)null),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (updated == 1 && deadLetter)
                    logger.LogError(ex, "导出 blob 删除进入死信 JobId={JobId} Attempt={Attempt}", claim.Id, attempt);
                else
                    logger.LogWarning(ex, "导出 blob 删除重试失败 JobId={JobId}", claim.Id);
            }
        }
    }

    private sealed record DeleteClaim(
        string Id,
        string? ObjectKey,
        string Status,
        string LeaseToken,
        int AttemptCount);
}
