using System.Buffers;
using System.Data.Common;
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
    IAttachmentContentScanner contentScanner,
    IOptions<AttachmentStorageOptions> options,
    ILogger<AttachmentScanService> logger,
    IAttachmentScanProjectionService projections) : IAttachmentScanService
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
        foreach (var job in claimed)
            await ProcessClaimedJobAsync(job, cancellationToken).ConfigureAwait(false);

        // The projection path is also drained here so single-scope test/admin calls
        // observe the same terminal behavior as the dedicated production worker.
        var projected = await projections.ProcessDueAsync(cancellationToken).ConfigureAwait(false);
        await PurgeOldDoneAsync(cancellationToken).ConfigureAwait(false);
        return projected;
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
    /// 处理单个已领取作业。执行内容扫描后，先以 LeaseToken fencing 将结论和审计
    /// 原子写入本地投递表；外部元数据写入由独立 projector 完成。
    /// </summary>
    public async Task<AttachmentScanProcessResult> ProcessClaimedJobAsync(
        AttachmentScanJob claimed, CancellationToken cancellationToken = default)
    {
        var maxAttempts = Math.Max(1, options.Value.MaxScanAttempts);

        try
        {
            var scanResult = await ExecuteScanAsync(claimed, cancellationToken).ConfigureAwait(false);
            if (scanResult.Transient)
            {
                await WriteScanAuditAsync(claimed, scanResult, cancellationToken).ConfigureAwait(false);
                return await ApplyTerminalAsync(
                        claimed,
                        ScanOutcome.Transient,
                        Truncate(scanResult.Error ?? "transient_scan_failure", 500),
                        maxAttempts,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var outcome = scanResult.Ok
                ? AttachmentScanProjectionOutcome.Confirmed
                : AttachmentScanProjectionOutcome.Rejected;
            var staged = await PersistFinalResultAsync(
                    claimed,
                    scanResult,
                    outcome,
                    claimed.AttemptCount,
                    cancellationToken)
                .ConfigureAwait(false);
            if (staged)
                AuthSecurityMetrics.AttachmentScan("result_staged");
            return staged
                ? AttachmentScanProcessResult.ResultStaged
                : AttachmentScanProcessResult.LeaseLost;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "附件扫描瞬时失败，保留可重试 JobId={Id} AttachmentId={Aid} Attempt={Attempt}",
                claimed.Id,
                claimed.AttachmentId,
                claimed.AttemptCount);
            var transient = new ContentScanResult(
                false, null, ex.Message, true, "unknown", "unknown");
            try
            {
                await WriteScanAuditAsync(claimed, transient, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception auditEx)
            {
                logger.LogWarning(auditEx, "附件扫描异常审计写入失败 JobId={Id}", claimed.Id);
            }

            return await ApplyTerminalAsync(
                    claimed,
                    ScanOutcome.Transient,
                    Truncate(ex.Message, 500),
                    maxAttempts,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<LeaseRenewalResult> RenewLeaseAsync(
        long jobId, string leaseOwner, string leaseToken, CancellationToken cancellationToken = default)
    {
        var until = DateTimeOffset.UtcNow.Add(Lease);
        try
        {
            if (!IsNpgsql())
            {
                var tracked = await db.AttachmentScanJobs
                    .FirstOrDefaultAsync(
                        j => j.Id == jobId
                             && j.LeaseOwner == leaseOwner
                             && j.LeaseToken == leaseToken
                             && (j.Status == AttachmentScanJobStatus.Processing
                                 || j.Status == AttachmentScanJobStatus.Finalizing),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (tracked is null)
                    return LeaseRenewalResult.LeaseLost;

                tracked.LeaseExpiresAt = until;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return LeaseRenewalResult.Renewed;
            }

            var updated = await db.AttachmentScanJobs
                .Where(j => j.Id == jobId
                    && j.LeaseOwner == leaseOwner
                    && j.LeaseToken == leaseToken
                    && (j.Status == AttachmentScanJobStatus.Processing
                        || j.Status == AttachmentScanJobStatus.Finalizing))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(j => j.LeaseExpiresAt, until),
                    cancellationToken)
                .ConfigureAwait(false);
            return updated == 1
                ? LeaseRenewalResult.Renewed
                : LeaseRenewalResult.LeaseLost;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            logger.LogDebug(ex, "附件扫描租约续租发生瞬时失败 JobId={Id}", jobId);
            return LeaseRenewalResult.TransientFailure;
        }
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
    /// 瞬时扫描失败沿 lease-fenced 路径回到 Pending；重试耗尽时同样先写入
    /// durable Rejected projection，禁止由扫描 worker 直接修改 Realtime 元数据。
    /// </summary>
    private async Task<AttachmentScanProcessResult> ApplyTerminalAsync(
        AttachmentScanJob claimed,
        ScanOutcome outcome,
        string? lastError,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        if (outcome != ScanOutcome.Transient)
            return AttachmentScanProcessResult.LeaseLost;

        var now = DateTimeOffset.UtcNow;
        var truncatedError = Truncate(lastError ?? "transient_scan_failure", 500);
        var attemptCount = Math.Max(1, claimed.AttemptCount + 1);
        AuthSecurityMetrics.AttachmentScan("retry");

        if (attemptCount < maxAttempts)
        {
            var nextAttemptAt = now.Add(ComputeBackoff(options.Value, attemptCount));
            var requeued = await ApplyFencedUpdateAsync(claimed, new TargetFields
            {
                Status = AttachmentScanJobStatus.Pending,
                CompletedAt = claimed.CompletedAt,
                AttemptCount = attemptCount,
                LastError = truncatedError,
                NextAttemptAt = nextAttemptAt,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAt = null,
            }, cancellationToken).ConfigureAwait(false);
            return requeued
                ? AttachmentScanProcessResult.RetryScheduled
                : AttachmentScanProcessResult.LeaseLost;
        }

        logger.LogError(
            "附件扫描重试已耗尽，写入拒绝投递记录 JobId={Id} AttachmentId={AttachmentId} Attempts={AttemptCount}",
            claimed.Id,
            claimed.AttachmentId,
            attemptCount);

        var exhausted = new ContentScanResult(
            false,
            null,
            truncatedError,
            false,
            "ChatApp.ContentPipeline",
            "exhausted");
        var staged = await PersistFinalResultAsync(
                claimed,
                exhausted,
                AttachmentScanProjectionOutcome.Rejected,
                attemptCount,
                cancellationToken)
            .ConfigureAwait(false);
        if (staged)
            AuthSecurityMetrics.AttachmentScan("exhausted_staged");
        return staged
            ? AttachmentScanProcessResult.ResultStaged
            : AttachmentScanProcessResult.LeaseLost;
    }

    /// <summary>
    /// Atomically changes the leased scan job to Finalizing and creates both the
    /// immutable audit row and the metadata projection. No external call occurs
    /// before this lease fence succeeds.
    /// </summary>
    private async Task<bool> PersistFinalResultAsync(
        AttachmentScanJob claimed,
        ContentScanResult result,
        string outcome,
        int attemptCount,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var finalContentType = result.ContentType
                               ?? (string.IsNullOrWhiteSpace(claimed.ContentType)
                                   ? "application/octet-stream"
                                   : claimed.ContentType);
        var rejectionReason = outcome == AttachmentScanProjectionOutcome.Rejected
            ? Truncate(result.Error ?? "rejected", 500)
            : null;
        var projection = new AttachmentScanProjection
        {
            ScanJobId = claimed.Id,
            AttachmentId = claimed.AttachmentId,
            ObjectKey = claimed.ObjectKey,
            UserId = claimed.UserId,
            ContentType = finalContentType,
            OriginalName = claimed.OriginalName,
            SizeBytes = claimed.SizeBytes,
            ContentHash = result.ContentHash,
            SourceEntityTag = result.SourceEntityTag,
            Outcome = outcome,
            RejectionReason = rejectionReason,
            AttemptCount = 0,
            NextAttemptAt = now,
            CreatedAt = now,
            Status = AttachmentScanProjectionStatus.Pending,
        };
        var audit = CreateScanAudit(claimed, result, attemptCount);

        if (!IsNpgsql())
        {
            var tracked = await db.AttachmentScanJobs
                .FirstOrDefaultAsync(j => j.Id == claimed.Id, cancellationToken)
                .ConfigureAwait(false);
            if (tracked is null
                || tracked.Status != AttachmentScanJobStatus.Processing
                || tracked.LeaseOwner != claimed.LeaseOwner
                || tracked.LeaseToken != claimed.LeaseToken)
                return false;

            tracked.Status = AttachmentScanJobStatus.Finalizing;
            tracked.CompletedAt = null;
            tracked.AttemptCount = attemptCount;
            tracked.LastError = rejectionReason;
            tracked.NextAttemptAt = now;
            tracked.LeaseOwner = null;
            tracked.LeaseToken = null;
            tracked.LeaseExpiresAt = null;
            db.AttachmentScanProjections.Add(projection);
            db.AttachmentScanAudits.Add(audit);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var updated = await db.AttachmentScanJobs
                .Where(j => j.Id == claimed.Id
                            && j.Status == AttachmentScanJobStatus.Processing
                            && j.LeaseOwner == claimed.LeaseOwner
                            && j.LeaseToken == claimed.LeaseToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, AttachmentScanJobStatus.Finalizing)
                    .SetProperty(j => j.CompletedAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.AttemptCount, attemptCount)
                    .SetProperty(j => j.LastError, rejectionReason)
                    .SetProperty(j => j.NextAttemptAt, now)
                    .SetProperty(j => j.LeaseOwner, (string?)null)
                    .SetProperty(j => j.LeaseToken, (string?)null)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null),
                    cancellationToken)
                .ConfigureAwait(false);
            if (updated != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            db.AttachmentScanProjections.Add(projection);
            db.AttachmentScanAudits.Add(audit);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

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

    private Task<ContentScanResult> ExecuteScanAsync(
        AttachmentScanJob claimed,
        CancellationToken cancellationToken) =>
        ScanContentAsync(
            claimed.ObjectKey,
            claimed.ContentType,
            claimed.OriginalName,
            claimed.SizeBytes,
            cancellationToken);

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

        var oldProjections = await db.AttachmentScanProjections
            .Where(x => x.Status == AttachmentScanProjectionStatus.Done
                        && x.CompletedAt != null
                        && x.CompletedAt < cutoff)
            .OrderBy(x => x.CompletedAt)
            .Take(200)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (oldProjections.Count > 0)
            db.AttachmentScanProjections.RemoveRange(oldProjections);

        if (old.Count > 0 || oldAudits.Count > 0 || oldProjections.Count > 0)
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

        return new ContentScanResult(
            true, finalType, null, false, auditEngine, auditVersion,
            contentHash, read.EntityTag);
    }

    private AttachmentScanAudit CreateScanAudit(
        AttachmentScanJob job,
        ContentScanResult result,
        int? attemptCount = null) =>
        new()
        {
            ScanJobId = job.Id,
            AttachmentId = job.AttachmentId,
            ObjectKey = job.ObjectKey,
            UserId = job.UserId,
            AttemptCount = attemptCount ?? Math.Max(1, job.AttemptCount),
            ContentType = result.ContentType ?? job.ContentType,
            SizeBytes = job.SizeBytes,
            EngineName = Truncate(result.EngineName ?? "unknown", 128),
            EngineVersion = Truncate(result.EngineVersion ?? "unknown", 128),
            Verdict = result.Transient ? "transient" : result.Ok ? "allowed" : "rejected",
            Allowed = result.Ok,
            IsTransient = result.Transient,
            Reason = result.Error is null ? null : Truncate(result.Error, 500),
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private async Task WriteScanAuditAsync(
        AttachmentScanJob job,
        ContentScanResult result,
        CancellationToken cancellationToken)
    {
        db.AttachmentScanAudits.Add(CreateScanAudit(job, result));
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
        string? EngineVersion,
        string? ContentHash = null,
        string? SourceEntityTag = null);
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
