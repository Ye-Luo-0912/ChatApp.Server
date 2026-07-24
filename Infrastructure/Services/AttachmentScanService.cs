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
    ILogger<AttachmentScanService> logger) : IAttachmentScanService
{
    private static readonly string ProcessOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}";

    private static readonly string[] ActiveStatuses =
    [
        AttachmentScanJobStatus.Pending,
        AttachmentScanJobStatus.Processing,
        AttachmentScanJobStatus.Finalizing
    ];

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

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var batchSize = Math.Clamp(opts.ScanBatchSize, 1, 200);
        var maxAttempts = Math.Max(1, opts.MaxScanAttempts);
        var now = DateTimeOffset.UtcNow;
        var leaseTtl = TimeSpan.FromMinutes(5);
        var owner = $"{ProcessOwner}:{Guid.NewGuid():N}";
        if (owner.Length > 128)
            owner = owner[..128];

        var claimed = await ClaimDueJobsAsync(batchSize, owner, now, leaseTtl, cancellationToken)
            .ConfigureAwait(false);
        if (claimed.Count == 0)
        {
            await PurgeOldDoneAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var completed = 0;
        foreach (var job in claimed)
        {
            try
            {
                var outcome = await ExecuteScanAsync(job, cancellationToken).ConfigureAwait(false);
                if (outcome == ScanOutcome.Transient)
                {
                    await ScheduleRetryAsync(job, opts, maxAttempts, job.LastError ?? "transient_scan_failure", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                // Confirmed / Rejected：元数据已成功写入，才允许 Done。
                MarkDone(job, outcome == ScanOutcome.Rejected ? Truncate(job.LastError ?? "rejected", 500) : null);
                AuthSecurityMetrics.AttachmentPendingScanDelta(-1);
                completed++;
            }
            catch (Exception ex)
            {
                await ScheduleRetryAsync(job, opts, maxAttempts, Truncate(ex.Message, 500), cancellationToken)
                    .ConfigureAwait(false);
                logger.LogWarning(
                    ex,
                    "附件扫描瞬时失败，保留可重试 JobId={Id} AttachmentId={Aid} Attempt={Attempt}",
                    job.Id,
                    job.AttachmentId,
                    job.AttemptCount);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await PurgeOldDoneAsync(cancellationToken).ConfigureAwait(false);
        return completed;
    }

    private async Task<List<AttachmentScanJob>> ClaimDueJobsAsync(
        int batchSize,
        string owner,
        DateTimeOffset now,
        TimeSpan leaseTtl,
        CancellationToken cancellationToken)
    {
        var leaseUntil = now.Add(leaseTtl);
        if (IsNpgsql())
        {
            var claimedIds = await ClaimDueJobIdsNpgsqlAsync(
                    batchSize, owner, now, leaseUntil, cancellationToken)
                .ConfigureAwait(false);
            if (claimedIds.Count == 0)
                return [];

            return await db.AttachmentScanJobs
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
            job.LeaseExpiresAt = leaseUntil;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return due;
    }

    private bool IsNpgsql() =>
        db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<List<long>> ClaimDueJobIdsNpgsqlAsync(
        int batchSize,
        string owner,
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

    private async Task ScheduleRetryAsync(
        AttachmentScanJob job,
        AttachmentStorageOptions opts,
        int maxAttempts,
        string error,
        CancellationToken cancellationToken)
    {
        job.AttemptCount = Math.Max(1, job.AttemptCount + 1);
        job.LastError = Truncate(error, 500);
        job.NextAttemptAt = DateTimeOffset.UtcNow.Add(ComputeBackoff(opts, job.AttemptCount));
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        AuthSecurityMetrics.AttachmentScan("retry");

        if (job.AttemptCount < maxAttempts)
        {
            job.Status = AttachmentScanJobStatus.Pending;
            return;
        }

        // 重试耗尽：必须先让 Realtime 进入 Rejected，作业才能 Done；否则 DeadLetter 以便人工/后续恢复。
        job.Status = AttachmentScanJobStatus.Finalizing;
        logger.LogError(
            "附件扫描重试已耗尽 JobId={Id} AttachmentId={Aid} Attempts={Attempt}",
            job.Id,
            job.AttachmentId,
            job.AttemptCount);

        if (!metadata.IsAvailable)
        {
            MarkDeadLetter(job, job.LastError ?? "扫描重试已耗尽且元数据不可用");
            return;
        }

        try
        {
            await metadata.MarkRejectedAsync(
                    job.AttachmentId,
                    job.UserId,
                    job.LastError ?? "扫描重试已耗尽",
                    cancellationToken)
                .ConfigureAwait(false);
            AuthSecurityMetrics.AttachmentScan("rejected");
            AuthSecurityMetrics.AttachmentScan("exhausted");
            MarkDone(job, job.LastError);
            AuthSecurityMetrics.AttachmentPendingScanDelta(-1);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "扫描耗尽后 MarkRejected 失败 AttachmentId={Id}", job.AttachmentId);
            MarkDeadLetter(job, Truncate($"exhausted_reject_failed:{ex.Message}", 500));
        }
    }

    private async Task<ScanOutcome> ExecuteScanAsync(
        AttachmentScanJob job,
        CancellationToken cancellationToken)
    {
        var (ok, sniffedType, error, transient) = await ScanContentAsync(
                job.ObjectKey,
                job.ContentType,
                job.OriginalName,
                job.SizeBytes,
                cancellationToken)
            .ConfigureAwait(false);

        if (transient)
        {
            job.LastError = Truncate(error ?? "transient", 500);
            return ScanOutcome.Transient;
        }

        job.Status = AttachmentScanJobStatus.Finalizing;
        job.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        if (!ok)
        {
            job.LastError = Truncate(error ?? "rejected", 500);
            if (!metadata.IsAvailable)
            {
                job.LastError = Truncate("rejected_but_metadata_unavailable", 500);
                return ScanOutcome.Transient;
            }

            try
            {
                await metadata.MarkRejectedAsync(
                        job.AttachmentId, job.UserId, job.LastError, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                job.LastError = Truncate($"reject_metadata_failed:{ex.Message}", 500);
                return ScanOutcome.Transient;
            }

            AuthSecurityMetrics.AttachmentScan("rejected");
            return ScanOutcome.Rejected;
        }

        if (!metadata.IsAvailable)
        {
            job.LastError = "metadata_unavailable";
            return ScanOutcome.Transient;
        }

        var finalContentType = sniffedType
                               ?? (string.IsNullOrWhiteSpace(job.ContentType)
                                   ? "application/octet-stream"
                                   : job.ContentType);

        try
        {
            await metadata.ConfirmAsync(
                    job.AttachmentId,
                    job.UserId,
                    job.ObjectKey,
                    publicUrl: null,
                    contentType: finalContentType,
                    sizeBytes: job.SizeBytes,
                    originalName: job.OriginalName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            job.LastError = Truncate($"confirm_metadata_failed:{ex.Message}", 500);
            return ScanOutcome.Transient;
        }

        AuthSecurityMetrics.AttachmentScan("confirmed");
        return ScanOutcome.Confirmed;
    }

    private static void MarkDone(AttachmentScanJob job, string? lastError)
    {
        job.Status = AttachmentScanJobStatus.Done;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.LastError = lastError;
    }

    private static void MarkDeadLetter(AttachmentScanJob job, string error)
    {
        job.Status = AttachmentScanJobStatus.DeadLetter;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.LastError = Truncate(error, 500);
        AuthSecurityMetrics.AttachmentPendingScanDelta(-1);
        AuthSecurityMetrics.AttachmentScan("dead_letter");
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
        if (old.Count == 0)
            return;

        db.AttachmentScanJobs.RemoveRange(old);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string? ContentType, string? Error, bool Transient)> ScanContentAsync(
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
            return (false, null, ex.Message, Transient: true);
        }

        if (read is null)
        {
            // 对象不可读（含 S3 NotFound）：保持 Scanning，瞬时重试；禁止扩展名-only 放行。
            return (false, null, "附件对象不可读，无法完成内容扫描", Transient: true);
        }

        string finalType;
        string? contentHash = null;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            await using (read.Content)
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
                    return (false, null, "无法识别或不支持的附件内容类型", Transient: false);

                // 单次流式管线：魔数 → SHA-256 → 大小校验 → AV（可 Seek 则回绕；否则用已读头）。
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
                        return (false, null, "附件大小超限", Transient: false);
                    hasher.AppendData(buffer.AsSpan(0, n));
                }

                Span<byte> hashBytes = stackalloc byte[32];
                if (!hasher.TryGetHashAndReset(hashBytes, out var written) || written != 32)
                    return (false, null, "附件哈希计算失败", Transient: true);
                contentHash = Convert.ToHexStringLower(hashBytes);

                if (claimedSize > 0 && Math.Abs(total - claimedSize) > Math.Max(1024, claimedSize / 10))
                    return (false, null, "附件大小与元数据不一致", Transient: false);

                AttachmentContentScanResult scan;
                if (read.Content.CanSeek)
                {
                    read.Content.Position = 0;
                    scan = await contentScanner.ScanAsync(
                            read.Content, finalType, originalName, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    // 非 Seek 流（如部分对象存储）：DenyList 仅需头 + 文件名，避免二次 OpenRead。
                    await using var headerStream = new MemoryStream(headerBuf, 0, headerLen, writable: false);
                    scan = await contentScanner.ScanAsync(
                            headerStream, finalType, originalName, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!scan.Allowed)
                {
                    if (scan.IsTransient)
                        return (false, null, scan.Reason, Transient: true);
                    return (false, null, scan.Reason ?? "附件内容扫描未通过", Transient: false);
                }
            }
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message, Transient: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        _ = contentHash; // 哈希已在单次读取中计算；content_hash 由上传路径写入元数据。
        return (true, finalType, null, Transient: false);
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
