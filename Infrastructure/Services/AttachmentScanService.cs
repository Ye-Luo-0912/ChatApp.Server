using System.Buffers;
using System.Security.Cryptography;
using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public sealed class AttachmentScanService(
    UserDbContext db,
    IAttachmentStorage storage,
    IAttachmentMetadataStore metadata,
    IAttachmentContentScanner contentScanner,
    IOptions<AttachmentStorageOptions> options,
    ILogger<AttachmentScanService> logger,
    AttachmentBlobDeleteEnqueuer? blobDeletes = null) : IAttachmentScanService
{
    private static readonly string ProcessOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}";

    private static readonly string[] ActiveStatuses =
    [
        AttachmentScanJobStatus.Pending,
        AttachmentScanJobStatus.Processing,
        AttachmentScanJobStatus.Finalizing
    ];

    /// <summary>P0-5.2：扫描租约时长（分钟）。Worker 心跳按 lease/3 续租，避免大文件扫描期间租约过期被重新领取。</summary>
    public const int LeaseMinutes = 5;

    /// <summary>P0-5.2：扫描租约时长。Worker 心跳按 lease/3 续租，避免大文件扫描期间租约过期被重新领取。</summary>
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(LeaseMinutes);

    public async Task EnqueueAsync(
        string attachmentId,
        long userId,
        string objectKey,
        string? contentType,
        string? originalName,
        long sizeBytes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(attachmentId) || string.IsNullOrWhiteSpace(objectKey))
            return;

        var id = attachmentId.Trim();
        var key = objectKey.Trim();
        var exists = await db.AttachmentScanJobs
            .AnyAsync(
                j => j.AttachmentId == id && ActiveStatuses.Contains(j.Status),
                cancellationToken)
            .ConfigureAwait(false);
        if (exists)
            return;

        var now = DateTimeOffset.UtcNow;
        db.AttachmentScanJobs.Add(new AttachmentScanJob
        {
            AttachmentId = id,
            ObjectKey = key,
            UserId = userId,
            ContentType = contentType,
            OriginalName = originalName,
            SizeBytes = sizeBytes,
            Status = AttachmentScanJobStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = now,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        AuthSecurityMetrics.AttachmentPendingScanDelta(1);
        AuthSecurityMetrics.AttachmentScan("enqueued");
        logger.LogInformation(
            "已入队附件扫描作业 AttachmentId={Id} UserId={UserId}",
            id,
            userId);
    }

    /// <summary>
    /// 串行处理到期作业（测试与单次排空入口）。生产 Worker 应使用
    /// <see cref="ClaimDueJobsAsync"/> + <see cref="ProcessClaimedJobAsync"/> 以获得有界并发与心跳续租。
    /// </summary>
    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var batchSize = Math.Clamp(options.Value.ScanBatchSize, 1, 200);
        var claimed = await ClaimDueJobsAsync(batchSize, cancellationToken).ConfigureAwait(false);
        if (claimed.Count == 0)
        {
            await PurgeOldDoneAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var completed = 0;
        foreach (var job in claimed)
        {
            if (await ProcessClaimedJobAsync(job, cancellationToken).ConfigureAwait(false))
                completed++;
        }

        await PurgeOldDoneAsync(cancellationToken).ConfigureAwait(false);
        return completed;
    }

    public async Task<IReadOnlyList<AttachmentScanJob>> ClaimDueJobsAsync(
        int batchSize, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(Lease);
        // P0-5.2：每次领取生成唯一 owner + LeaseToken 作为 fencing token。
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

            return await db.AttachmentScanJobs
                .AsNoTracking()
                .Where(j => claimedIds.Contains(j.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        // InMemory / 单测：同进程内先标记 Processing 再扫描。
        var due = await db.AttachmentScanJobs
            .Where(j =>
                (j.Status == AttachmentScanJobStatus.Pending && j.NextAttemptAt <= now)
                || ((j.Status == AttachmentScanJobStatus.Processing
                     || j.Status == AttachmentScanJobStatus.Finalizing)
                    && j.LeaseExpiresAt != null
                    && j.LeaseExpiresAt < now))
            .OrderBy(j => j.NextAttemptAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (due.Count == 0)
            return due;

        foreach (var job in due)
        {
            job.Status = AttachmentScanJobStatus.Processing;
            job.LeaseOwner = owner;
            job.LeaseToken = leaseToken;
            job.LeaseExpiresAt = leaseUntil;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return due;
    }

    /// <summary>
    /// 处理单个已领取作业。执行内容扫描与元数据写入，随后以 LeaseToken fencing 落终态。
    /// 租约已易主（被重新领取）时终态更新命中 0 行，返回 false 且不重复写元数据终态。
    /// </summary>
    public async Task<bool> ProcessClaimedJobAsync(
        AttachmentScanJob claimed, CancellationToken cancellationToken = default)
    {
        var maxAttempts = Math.Max(1, options.Value.MaxScanAttempts);

        ScanOutcome outcome;
        string? lastError;
        try
        {
            (outcome, lastError) = await ExecuteScanAsync(claimed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 扫描本身异常：按瞬时失败走 fenced 重试路径，避免旧租约覆盖。
            logger.LogWarning(
                ex,
                "附件扫描瞬时失败，保留可重试 JobId={Id} AttachmentId={Aid} Attempt={Attempt}",
                claimed.Id,
                claimed.AttachmentId,
                claimed.AttemptCount);
            try
            {
                await WriteScanAuditAsync(
                        claimed,
                        new ContentScanResult(false, null, ex.Message, true, "unknown", "unknown"),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception auditEx)
            {
                logger.LogWarning(auditEx, "附件扫描异常审计写入失败 JobId={Id}", claimed.Id);
            }
            return await ApplyTerminalAsync(
                claimed, ScanOutcome.Transient, Truncate(ex.Message, 500), maxAttempts, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ApplyTerminalAsync(claimed, outcome, lastError, maxAttempts, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<int> RenewLeaseAsync(
        long jobId, string leaseOwner, string leaseToken, CancellationToken cancellationToken = default)
    {
        var until = DateTimeOffset.UtcNow.Add(Lease);
        return db.AttachmentScanJobs
            .Where(j => j.Id == jobId
                && j.LeaseOwner == leaseOwner
                && j.LeaseToken == leaseToken
                && (j.Status == AttachmentScanJobStatus.Processing
                    || j.Status == AttachmentScanJobStatus.Finalizing))
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.LeaseExpiresAt, until),
                cancellationToken);
    }

    private bool IsNpgsql() =>
        db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

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
        command.CommandText =
            """
            UPDATE "T_AttachmentScanJob" AS j
            SET "Status" = 'Processing',
                "LeaseOwner" = @owner,
                "LeaseToken" = @lease_token,
                "LeaseExpiresAt" = @lease_until
            WHERE j."Id" IN (
                SELECT c."Id"
                FROM "T_AttachmentScanJob" AS c
                WHERE (c."Status" = 'Pending' AND c."NextAttemptAt" <= @now)
                   OR (c."Status" IN ('Processing', 'Finalizing')
                       AND c."LeaseExpiresAt" IS NOT NULL
                       AND c."LeaseExpiresAt" < @now)
                ORDER BY c."NextAttemptAt"
                FOR UPDATE SKIP LOCKED
                LIMIT @batch
            )
            RETURNING j."Id";
            """;

        var pOwner = command.CreateParameter();
        pOwner.ParameterName = "owner";
        pOwner.Value = owner;
        command.Parameters.Add(pOwner);

        var pToken = command.CreateParameter();
        pToken.ParameterName = "lease_token";
        pToken.Value = leaseToken;
        command.Parameters.Add(pToken);

        var pLease = command.CreateParameter();
        pLease.ParameterName = "lease_until";
        pLease.Value = leaseUntil;
        command.Parameters.Add(pLease);

        var pNow = command.CreateParameter();
        pNow.ParameterName = "now";
        pNow.Value = now;
        command.Parameters.Add(pNow);

        var pBatch = command.CreateParameter();
        pBatch.ParameterName = "batch";
        pBatch.Value = batchSize;
        command.Parameters.Add(pBatch);

        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            ids.Add(reader.GetInt64(0));

        return ids;
    }

    /// <summary>
    /// 以 LeaseToken fencing 落终态。构建 target 快照后调用 <see cref="ApplyFencedUpdateAsync"/>：
    /// Npgsql 走 ExecuteUpdateAsync（匹配 Id+Status(Processing/Finalizing)+LeaseOwner+LeaseToken）；
    /// InMemory（单测）走 tracked 重载 + lease 校验 + SaveChanges。
    /// 返回 true 表示进入终态（Done/DeadLetter），false 表示已重试为 Pending 或租约丢失。
    /// </summary>
    private async Task<bool> ApplyTerminalAsync(
        AttachmentScanJob claimed,
        ScanOutcome outcome,
        string? lastError,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var truncatedError = lastError is null ? null : Truncate(lastError, 500);

        if (outcome is ScanOutcome.Confirmed or ScanOutcome.Rejected)
        {
            var done = await ApplyFencedUpdateAsync(claimed, new TargetFields
            {
                Status = AttachmentScanJobStatus.Done,
                CompletedAt = now,
                AttemptCount = claimed.AttemptCount,
                LastError = outcome == ScanOutcome.Rejected ? truncatedError : null,
                NextAttemptAt = claimed.NextAttemptAt,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAt = null,
            }, cancellationToken).ConfigureAwait(false);
            if (done)
                AuthSecurityMetrics.AttachmentPendingScanDelta(-1);
            return done;
        }

        // Transient：重试或耗尽。
        var attemptCount = Math.Max(1, claimed.AttemptCount + 1);
        AuthSecurityMetrics.AttachmentScan("retry");

        if (attemptCount < maxAttempts)
        {
            var nextAttemptAt = now.Add(ComputeBackoff(options.Value, attemptCount));
            await ApplyFencedUpdateAsync(claimed, new TargetFields
            {
                Status = AttachmentScanJobStatus.Pending,
                CompletedAt = claimed.CompletedAt,
                AttemptCount = attemptCount,
                LastError = truncatedError ?? "transient_scan_failure",
                NextAttemptAt = nextAttemptAt,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAt = null,
            }, cancellationToken).ConfigureAwait(false);
            return false;
        }

        // 重试耗尽：先让 Realtime 进入 Rejected，作业才 Done；元数据不可用则 DeadLetter。
        logger.LogError(
            "附件扫描重试已耗尽 JobId={Id} AttachmentId={Aid} Attempts={Attempt}",
            claimed.Id,
            claimed.AttachmentId,
            attemptCount);

        var deleteQueued = await TryEnqueueRejectedBlobDeleteAsync(
                claimed, cancellationToken)
            .ConfigureAwait(false);

        if (!metadata.IsAvailable)
        {
            var dead = await ApplyFencedUpdateAsync(claimed, new TargetFields
            {
                Status = AttachmentScanJobStatus.DeadLetter,
                CompletedAt = now,
                AttemptCount = attemptCount,
                LastError = Truncate(
                    (truncatedError ?? "扫描重试已耗尽且元数据不可用")
                    + (deleteQueued ? string.Empty : ";blob_delete_enqueue_failed"),
                    500),
                NextAttemptAt = claimed.NextAttemptAt,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAt = null,
            }, cancellationToken).ConfigureAwait(false);
            if (dead)
            {
                AuthSecurityMetrics.AttachmentPendingScanDelta(-1);
                AuthSecurityMetrics.AttachmentScan("dead_letter");
            }
            return dead;
        }

        try
        {
            await metadata.MarkRejectedAsync(
                    claimed.AttachmentId,
                    claimed.UserId,
                    truncatedError ?? "扫描重试已耗尽",
                    cancellationToken)
                .ConfigureAwait(false);
            AuthSecurityMetrics.AttachmentScan("rejected");
            AuthSecurityMetrics.AttachmentScan("exhausted");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "扫描耗尽后 MarkRejected 失败 AttachmentId={Id}", claimed.AttachmentId);
            var dead = await ApplyFencedUpdateAsync(claimed, new TargetFields
            {
                Status = AttachmentScanJobStatus.DeadLetter,
                CompletedAt = now,
                AttemptCount = attemptCount,
                LastError = Truncate($"exhausted_reject_failed:{ex.Message}", 500),
                NextAttemptAt = claimed.NextAttemptAt,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAt = null,
            }, cancellationToken).ConfigureAwait(false);
            if (dead)
            {
                AuthSecurityMetrics.AttachmentPendingScanDelta(-1);
                AuthSecurityMetrics.AttachmentScan("dead_letter");
            }
            return dead;
        }

        var doneExhausted = await ApplyFencedUpdateAsync(claimed, new TargetFields
        {
            Status = deleteQueued
                ? AttachmentScanJobStatus.Done
                : AttachmentScanJobStatus.DeadLetter,
            CompletedAt = now,
            AttemptCount = attemptCount,
            LastError = deleteQueued
                ? truncatedError
                : Truncate((truncatedError ?? "扫描重试已耗尽") + ";blob_delete_enqueue_failed", 500),
            NextAttemptAt = claimed.NextAttemptAt,
            LeaseOwner = null,
            LeaseToken = null,
            LeaseExpiresAt = null,
        }, cancellationToken).ConfigureAwait(false);
        if (doneExhausted)
        {
            AuthSecurityMetrics.AttachmentPendingScanDelta(-1);
            if (!deleteQueued)
                AuthSecurityMetrics.AttachmentScan("dead_letter");
        }
        return doneExhausted;
    }

    private async Task<bool> TryEnqueueRejectedBlobDeleteAsync(
        AttachmentScanJob job,
        CancellationToken cancellationToken)
    {
        if (blobDeletes is null)
            return true;

        try
        {
            await blobDeletes.EnqueueAsync(
                    [(job.ObjectKey, job.AttachmentId)],
                    job.UserId,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "扫描重试耗尽后附件删除任务入队失败 AttachmentId={AttachmentId} Key={Key}",
                job.AttachmentId,
                job.ObjectKey);
            return false;
        }
    }

    /// <summary>终态字段快照（仅 AttachmentScanJob 的可变字段）。</summary>
    private sealed class TargetFields
    {
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset? CompletedAt { get; set; }
        public int AttemptCount { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset NextAttemptAt { get; set; }
        public string? LeaseOwner { get; set; }
        public string? LeaseToken { get; set; }
        public DateTimeOffset? LeaseExpiresAt { get; set; }
    }

    /// <summary>
    /// Fenced 终态更新。Npgsql：ExecuteUpdateAsync 匹配 Id+Status(Processing/Finalizing)+LeaseOwner+LeaseToken，命中 1 行返回 true。
    /// InMemory：重载 tracked 实体校验 lease 后写回 target 字段 + SaveChanges（单测单线程）。
    /// </summary>
    private async Task<bool> ApplyFencedUpdateAsync(
        AttachmentScanJob claimed, TargetFields target, CancellationToken cancellationToken)
    {
        if (!IsNpgsql())
        {
            var tracked = await db.AttachmentScanJobs
                .FirstOrDefaultAsync(j => j.Id == claimed.Id, cancellationToken)
                .ConfigureAwait(false);
            if (tracked is null
                || tracked.LeaseOwner != claimed.LeaseOwner
                || tracked.LeaseToken != claimed.LeaseToken
                || (tracked.Status != AttachmentScanJobStatus.Processing
                    && tracked.Status != AttachmentScanJobStatus.Finalizing))
                return false;

            tracked.Status = target.Status;
            tracked.CompletedAt = target.CompletedAt;
            tracked.AttemptCount = target.AttemptCount;
            tracked.LastError = target.LastError;
            tracked.NextAttemptAt = target.NextAttemptAt;
            tracked.LeaseOwner = target.LeaseOwner;
            tracked.LeaseToken = target.LeaseToken;
            tracked.LeaseExpiresAt = target.LeaseExpiresAt;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var n = await db.AttachmentScanJobs
            .Where(j => j.Id == claimed.Id
                && (j.Status == AttachmentScanJobStatus.Processing
                    || j.Status == AttachmentScanJobStatus.Finalizing)
                && j.LeaseOwner == claimed.LeaseOwner
                && j.LeaseToken == claimed.LeaseToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, target.Status)
                .SetProperty(j => j.CompletedAt, target.CompletedAt)
                .SetProperty(j => j.AttemptCount, target.AttemptCount)
                .SetProperty(j => j.LastError, target.LastError)
                .SetProperty(j => j.NextAttemptAt, target.NextAttemptAt)
                .SetProperty(j => j.LeaseOwner, target.LeaseOwner)
                .SetProperty(j => j.LeaseToken, target.LeaseToken)
                .SetProperty(j => j.LeaseExpiresAt, target.LeaseExpiresAt),
                cancellationToken)
            .ConfigureAwait(false);
        return n == 1;
    }

    private async Task<(ScanOutcome Outcome, string? LastError)> ExecuteScanAsync(
        AttachmentScanJob claimed,
        CancellationToken cancellationToken)
    {
        var scanResult = await ScanContentAsync(
                claimed.ObjectKey,
                claimed.ContentType,
                claimed.OriginalName,
                claimed.SizeBytes,
                cancellationToken)
            .ConfigureAwait(false);

        // 先持久化本次尝试，再执行 Realtime 的 Confirm/Reject，避免“状态已终结但审计写失败”。
        await WriteScanAuditAsync(claimed, scanResult, cancellationToken).ConfigureAwait(false);

        var (ok, sniffedType, error, transient, _, _) = scanResult;

        if (transient)
        {
            return (ScanOutcome.Transient, Truncate(error ?? "transient", 500));
        }

        if (!ok)
        {
            var rejectError = Truncate(error ?? "rejected", 500);
            if (!metadata.IsAvailable)
            {
                return (ScanOutcome.Transient, Truncate("rejected_but_metadata_unavailable", 500));
            }

            try
            {
                if (storage is IAttachmentScanStateMarker marker)
                    await marker.MarkScanStateAsync(
                            claimed.ObjectKey, "rejected", cancellationToken)
                        .ConfigureAwait(false);
                await metadata.MarkRejectedAsync(
                        claimed.AttachmentId, claimed.UserId, rejectError, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return (ScanOutcome.Transient, Truncate($"reject_metadata_failed:{ex.Message}", 500));
            }

            if (blobDeletes is not null)
            {
                try
                {
                    await blobDeletes.EnqueueAsync(
                            [(claimed.ObjectKey, claimed.AttachmentId)],
                            claimed.UserId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return (ScanOutcome.Transient, Truncate($"reject_delete_enqueue_failed:{ex.Message}", 500));
                }
            }

            AuthSecurityMetrics.AttachmentScan("rejected");
            return (ScanOutcome.Rejected, rejectError);
        }

        if (!metadata.IsAvailable)
        {
            return (ScanOutcome.Transient, "metadata_unavailable");
        }

        var finalContentType = sniffedType
                               ?? (string.IsNullOrWhiteSpace(claimed.ContentType)
                                   ? "application/octet-stream"
                                   : claimed.ContentType);

        try
        {
            if (storage is IAttachmentScanStateMarker marker)
                await marker.MarkScanStateAsync(
                        claimed.ObjectKey, "confirmed", cancellationToken)
                    .ConfigureAwait(false);
            await metadata.ConfirmAsync(
                    claimed.AttachmentId,
                    claimed.UserId,
                    claimed.ObjectKey,
                    publicUrl: null,
                    contentType: finalContentType,
                    sizeBytes: claimed.SizeBytes,
                    originalName: claimed.OriginalName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (ScanOutcome.Transient, Truncate($"confirm_metadata_failed:{ex.Message}", 500));
        }

        AuthSecurityMetrics.AttachmentScan("confirmed");
        return (ScanOutcome.Confirmed, null);
    }

    private async Task PurgeOldDoneAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        var old = await db.AttachmentScanJobs
            .Where(j => j.Status == AttachmentScanJobStatus.Done
                        && j.CompletedAt != null
                        && j.CompletedAt < cutoff)
            .OrderBy(j => j.CompletedAt)
            .Take(200)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (old.Count > 0)
            db.AttachmentScanJobs.RemoveRange(old);

        var auditCutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, options.Value.ScanAuditRetentionDays));
        var oldAudits = await db.AttachmentScanAudits
            .Where(x => x.CreatedAt < auditCutoff)
            .OrderBy(x => x.CreatedAt)
            .Take(1000)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (oldAudits.Count > 0)
            db.AttachmentScanAudits.RemoveRange(oldAudits);

        if (old.Count > 0 || oldAudits.Count > 0)
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ContentScanResult> ScanContentAsync(
        string objectKey,
        string? claimedContentType,
        string? originalName,
        long claimedSize,
        CancellationToken cancellationToken)
    {
        AttachmentReadResult? read;
        try
        {
            read = await storage.OpenReadAsync(objectKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ContentScanResult(
                false, null, ex.Message, true, "unknown", "unknown");
        }

        if (read is null)
        {
            // 对象不可读（含 S3 NotFound）：保持 Scanning，瞬时重试；禁止扩展名-only 放行。
            return new ContentScanResult(
                false, null, "附件对象不可读，无法完成内容扫描", true,
                "ChatApp.ContentPipeline", "1");
        }

        string finalType;
        var auditEngine = "ChatApp.ContentPipeline";
        var auditVersion = "1";
        string? contentHash = null;
        var scanPath = Path.Combine(
            Path.GetTempPath(),
            $"chatapp-attachment-scan-{Guid.NewGuid():N}.blob");
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            await using (read.Content)
            await using (var scanFile = new FileStream(
                             scanPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var headerLen = 0;
                var headerBuf = new byte[16];
                while (headerLen < headerBuf.Length)
                {
                    var n = await read.Content.ReadAsync(
                            headerBuf.AsMemory(headerLen, headerBuf.Length - headerLen),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (n == 0) break;
                    headerLen += n;
                }

                finalType = AttachmentMagicSniffer.Sniff(headerBuf.AsSpan(0, headerLen))
                            ?? "application/octet-stream";
                if (!storage.IsAllowedContentType(finalType))
                    return new ContentScanResult(
                        false, null, "无法识别或不支持的附件内容类型", false,
                        auditEngine, auditVersion);

                // 单次流式管线：魔数 → SHA-256/大小校验 → 受控临时文件 →
                // policy/AV。S3 response streams are commonly non-seekable; the
                // temporary file keeps scanning full-content without a large byte[].
                await scanFile.WriteAsync(
                        headerBuf.AsMemory(0, headerLen), cancellationToken)
                    .ConfigureAwait(false);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hasher.AppendData(headerBuf.AsSpan(0, headerLen));
                long total = headerLen;
                var max = options.Value.MaxBytes;
                while (true)
                {
                    var n = await read.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (n == 0) break;
                    total += n;
                    if (total > max)
                        return new ContentScanResult(
                            false, null, "附件大小超限", false, auditEngine, auditVersion);
                    hasher.AppendData(buffer.AsSpan(0, n));
                    await scanFile.WriteAsync(
                            buffer.AsMemory(0, n), cancellationToken)
                        .ConfigureAwait(false);
                }

                var hashBytes = new byte[32];
                if (!hasher.TryGetHashAndReset(hashBytes, out var written) || written != 32)
                    return new ContentScanResult(
                        false, null, "附件哈希计算失败", true, auditEngine, auditVersion);
                contentHash = Convert.ToHexStringLower(hashBytes);

                if (claimedSize > 0 && Math.Abs(total - claimedSize) > Math.Max(1024, claimedSize / 10))
                    return new ContentScanResult(
                        false, null, "附件大小与元数据不一致", false, auditEngine, auditVersion);

                await scanFile.FlushAsync(cancellationToken).ConfigureAwait(false);
                scanFile.Position = 0;
                var scan = await contentScanner.ScanAsync(
                        scanFile, finalType, originalName, cancellationToken)
                    .ConfigureAwait(false);
                auditEngine = scan.EngineName ?? auditEngine;
                auditVersion = scan.EngineVersion ?? auditVersion;
                logger.LogInformation(
                    "附件扫描审计 ObjectKey={ObjectKey} Engine={Engine} Version={Version} Allowed={Allowed} Reason={Reason}",
                    objectKey,
                    scan.EngineName ?? "unknown",
                    scan.EngineVersion ?? "unknown",
                    scan.Allowed,
                    scan.Reason);

                if (!scan.Allowed)
                {
                    if (scan.IsTransient)
                        return new ContentScanResult(
                            false, null, scan.Reason, true, auditEngine, auditVersion);
                    return new ContentScanResult(
                        false, null, scan.Reason ?? "附件内容扫描未通过", false,
                        auditEngine, auditVersion);
                }
            }
        }
        catch (Exception ex)
        {
            return new ContentScanResult(
                false, null, ex.Message, true, auditEngine, auditVersion);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            try
            {
                if (File.Exists(scanPath))
                    File.Delete(scanPath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "删除附件扫描临时文件失败 Path={Path}", scanPath);
            }
        }

        _ = contentHash; // 哈希已在单次读取中计算；content_hash 由上传路径写入元数据。
        return new ContentScanResult(
            true, finalType, null, false, auditEngine, auditVersion);
    }

    private async Task WriteScanAuditAsync(
        AttachmentScanJob job,
        ContentScanResult result,
        CancellationToken cancellationToken)
    {
        db.AttachmentScanAudits.Add(new AttachmentScanAudit
        {
            ScanJobId = job.Id,
            AttachmentId = job.AttachmentId,
            ObjectKey = job.ObjectKey,
            UserId = job.UserId,
            AttemptCount = Math.Max(1, job.AttemptCount),
            ContentType = job.ContentType,
            SizeBytes = job.SizeBytes,
            EngineName = Truncate(result.EngineName ?? "unknown", 128),
            EngineVersion = Truncate(result.EngineVersion ?? "unknown", 128),
            Verdict = result.Transient ? "transient" : result.Ok ? "allowed" : "rejected",
            Allowed = result.Ok,
            IsTransient = result.Transient,
            Reason = result.Error is null ? null : Truncate(result.Error, 500),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan ComputeBackoff(AttachmentStorageOptions opts, int attemptCount)
    {
        var baseSeconds = Math.Max(5, opts.ScanBackoffSeconds);
        var exp = Math.Min(attemptCount - 1, 10);
        var seconds = Math.Min(3600, baseSeconds * Math.Pow(2, Math.Max(0, exp)));
        return TimeSpan.FromSeconds(seconds);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    private enum ScanOutcome
    {
        Confirmed,
        Rejected,
        Transient,
    }

    private sealed record ContentScanResult(
        bool Ok,
        string? ContentType,
        string? Error,
        bool Transient,
        string? EngineName,
        string? EngineVersion);
}

/// <summary>DI 作用域工厂封装，供 BackgroundService / Confirm 路径入队。</summary>
public sealed class AttachmentScanEnqueuer(IServiceScopeFactory scopeFactory)
{
    public async Task EnqueueAsync(
        string attachmentId,
        long userId,
        string objectKey,
        string? contentType,
        string? originalName,
        long sizeBytes,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAttachmentScanService>();
        await svc.EnqueueAsync(
                attachmentId, userId, objectKey, contentType, originalName, sizeBytes, cancellationToken)
            .ConfigureAwait(false);
    }
}
