using Core.Interfaces;
using Core.Models.Export;
using Core.Models.Security;
using Core.Settings;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public interface IAttachmentOpsAdminService
{
    Task<AttachmentOpsOrphansDto> GetOrphansAsync(CancellationToken cancellationToken = default);

    Task<AttachmentOpsDeleteFailuresDto> GetDeleteFailuresAsync(CancellationToken cancellationToken = default);

    Task<AttachmentOpsScanBacklogDto> GetScanBacklogAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AttachmentScanAuditDto>> GetScanAuditsAsync(
        string attachmentId,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<AttachmentOpsHintsDto> GetHintsAsync(CancellationToken cancellationToken = default);

    Task<bool> RescanAsync(
        long adminUserId,
        string attachmentId,
        string? reason,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        long adminUserId,
        string attachmentId,
        string? reason,
        CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(
        long adminUserId,
        string attachmentId,
        string? reason,
        CancellationToken cancellationToken = default);
}

public sealed class AttachmentOpsAdminService(
    UserDbContext db,
    IAttachmentMetadataStore metadata,
    IOptions<AttachmentStorageOptions> options,
    IAttachmentBlobDeleteService? blobDeletes = null,
    IAttachmentStorage? storage = null,
    ILogger<AttachmentOpsAdminService>? logger = null) : IAttachmentOpsAdminService
{
    private static readonly string[] RelatedMetricNames =
    [
        "attachment.blob_delete",
        "attachment.scan",
        "attachment.pending_delete",
        "attachment.pending_scan",
    ];

    public async Task<AttachmentOpsOrphansDto> GetOrphansAsync(CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var orphanMinutes = Math.Max(30, opts.TicketMinutes * 4);
        var stuckMinutes = Math.Clamp(opts.StuckScanningMinutes, 1, 24 * 60);
        var sampleLimit = Math.Clamp(opts.OpsSampleLimit, 1, 20);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var raw = await metadata.QueryOpsOrphansAsync(
                TimeSpan.FromMinutes(orphanMinutes),
                TimeSpan.FromMinutes(stuckMinutes),
                sampleLimit,
                cancellationToken)
            .ConfigureAwait(false);

        return new AttachmentOpsOrphansDto(
            MetadataAvailable: raw.Available,
            UnavailableReason: raw.UnavailableReason,
            OrphanAgeMinutes: orphanMinutes,
            StuckScanningMinutes: stuckMinutes,
            ConfirmedUnboundPastAgeCount: raw.ConfirmedUnboundPastAgeCount,
            AbandonedUploadingPastAgeCount: raw.AbandonedUploadingPastAgeCount,
            StuckScanningCount: raw.StuckScanningCount,
            OldestConfirmedUnboundAgeMs: AgeMs(nowMs, raw.OldestConfirmedUnboundAtMs),
            OldestUploadingAgeMs: AgeMs(nowMs, raw.OldestUploadingAtMs),
            OldestStuckScanningAgeMs: AgeMs(nowMs, raw.OldestStuckScanningAtMs),
            WorstConfirmedUnbound: MapSamples(raw.WorstConfirmedUnbound, nowMs),
            WorstUploading: MapSamples(raw.WorstUploading, nowMs),
            WorstStuckScanning: MapSamples(raw.WorstStuckScanning, nowMs),
            GeneratedAtMs: nowMs);
    }

    public async Task<AttachmentOpsDeleteFailuresDto> GetDeleteFailuresAsync(
        CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var highThreshold = Math.Clamp(opts.OpsHighDeleteAttemptThreshold, 1, Math.Max(1, opts.MaxDeleteAttempts));
        var sampleLimit = Math.Clamp(opts.OpsSampleLimit, 1, 20);
        var now = DateTimeOffset.UtcNow;
        var pending = AttachmentBlobDeleteJobStatus.Pending;
        var done = AttachmentBlobDeleteJobStatus.Done;

        var agg = await db.AttachmentBlobDeleteJobs.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                PendingCount = g.Count(x => x.Status == pending),
                DoneCount = g.Count(x => x.Status == done),
                HighAttemptPendingCount = g.Count(x =>
                    x.Status == pending && x.AttemptCount >= highThreshold),
                MaxAttemptCount = g.Where(x => x.Status == pending).Max(x => (int?)x.AttemptCount) ?? 0,
                OldestPendingAt = g.Where(x => x.Status == pending).Min(x => (DateTimeOffset?)x.CreatedAt),
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var worst = await db.AttachmentBlobDeleteJobs.AsNoTracking()
            .Where(x => x.Status == pending)
            .OrderByDescending(x => x.AttemptCount)
            .ThenBy(x => x.CreatedAt)
            .Take(sampleLimit)
            .Select(x => new AttachmentOpsDeleteJobRowDto(
                x.Id,
                x.ObjectKey,
                x.AttachmentId,
                x.UserId,
                x.Status,
                x.AttemptCount,
                x.NextAttemptAt,
                x.CreatedAt,
                x.LastError))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        long? oldestAtMs = agg?.OldestPendingAt is { } oldest
            ? oldest.ToUnixTimeMilliseconds()
            : null;
        long? oldestAge = oldestAtMs is { } at
            ? Math.Max(0, now.ToUnixTimeMilliseconds() - at)
            : null;

        return new AttachmentOpsDeleteFailuresDto(
            PendingCount: agg?.PendingCount ?? 0,
            DoneCount: agg?.DoneCount ?? 0,
            HighAttemptPendingCount: agg?.HighAttemptPendingCount ?? 0,
            HighAttemptThreshold: highThreshold,
            MaxAttemptCount: agg?.MaxAttemptCount ?? 0,
            OldestPendingAtMs: oldestAtMs,
            OldestPendingAgeMs: oldestAge,
            WorstPending: worst,
            GeneratedAtMs: now.ToUnixTimeMilliseconds());
    }

    public async Task<AttachmentOpsScanBacklogDto> GetScanBacklogAsync(
        CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var maxAttempts = Math.Max(1, opts.MaxScanAttempts);
        var sampleLimit = Math.Clamp(opts.OpsSampleLimit, 1, 20);
        var now = DateTimeOffset.UtcNow;
        var pending = AttachmentScanJobStatus.Pending;
        var processing = AttachmentScanJobStatus.Processing;
        var finalizing = AttachmentScanJobStatus.Finalizing;
        var dead = AttachmentScanJobStatus.DeadLetter;
        var done = AttachmentScanJobStatus.Done;

        var agg = await db.AttachmentScanJobs.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                PendingCount = g.Count(x => x.Status == pending),
                ProcessingCount = g.Count(x => x.Status == processing),
                FinalizingCount = g.Count(x => x.Status == finalizing),
                DeadLetterCount = g.Count(x => x.Status == dead),
                DoneCount = g.Count(x => x.Status == done),
                RetryingCount = g.Count(x =>
                    x.Status == pending && x.AttemptCount > 0 && x.AttemptCount < maxAttempts),
                ExhaustedLikeCount = g.Count(x =>
                    x.Status == dead
                    || (x.Status == pending && x.AttemptCount >= maxAttempts)),
                OldestPendingAt = g.Where(x => x.Status == pending).Min(x => (DateTimeOffset?)x.CreatedAt),
                OldestProcessingAt = g.Where(x => x.Status == processing || x.Status == finalizing)
                    .Min(x => (DateTimeOffset?)x.CreatedAt),
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var openStatuses = new[] { pending, processing, finalizing, dead };
        var worst = await db.AttachmentScanJobs.AsNoTracking()
            .Where(x => openStatuses.Contains(x.Status))
            .OrderByDescending(x => x.AttemptCount)
            .ThenBy(x => x.CreatedAt)
            .Take(sampleLimit)
            .Select(x => new AttachmentOpsScanJobRowDto(
                x.Id,
                x.AttachmentId,
                x.ObjectKey,
                x.UserId,
                x.Status,
                x.AttemptCount,
                x.NextAttemptAt,
                x.CreatedAt,
                x.LeaseExpiresAt,
                x.LastError))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var nowMs = now.ToUnixTimeMilliseconds();
        long? oldestPendingMs = agg?.OldestPendingAt?.ToUnixTimeMilliseconds();
        long? oldestProcessingMs = agg?.OldestProcessingAt?.ToUnixTimeMilliseconds();

        return new AttachmentOpsScanBacklogDto(
            PendingCount: agg?.PendingCount ?? 0,
            ProcessingCount: agg?.ProcessingCount ?? 0,
            FinalizingCount: agg?.FinalizingCount ?? 0,
            DeadLetterCount: agg?.DeadLetterCount ?? 0,
            DoneCount: agg?.DoneCount ?? 0,
            RetryingCount: agg?.RetryingCount ?? 0,
            ExhaustedLikeCount: agg?.ExhaustedLikeCount ?? 0,
            MaxScanAttempts: maxAttempts,
            OldestPendingAtMs: oldestPendingMs,
            OldestPendingAgeMs: AgeMs(nowMs, oldestPendingMs),
            OldestProcessingAtMs: oldestProcessingMs,
            OldestProcessingAgeMs: AgeMs(nowMs, oldestProcessingMs),
            WorstOpen: worst,
            GeneratedAtMs: nowMs);
    }

    public async Task<IReadOnlyList<AttachmentScanAuditDto>> GetScanAuditsAsync(
        string attachmentId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(attachmentId))
            return [];

        var take = Math.Clamp(limit, 1, 200);
        return await db.AttachmentScanAudits.AsNoTracking()
            .Where(x => x.AttachmentId == attachmentId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(take)
            .Select(x => new AttachmentScanAuditDto(
                x.Id,
                x.ScanJobId,
                x.AttachmentId,
                x.ObjectKey,
                x.UserId,
                x.AttemptCount,
                x.ContentType,
                x.SizeBytes,
                x.EngineName,
                x.EngineVersion,
                x.Verdict,
                x.Allowed,
                x.IsTransient,
                x.Reason,
                x.CreatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AttachmentOpsHintsDto> GetHintsAsync(CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var orphanMinutes = Math.Max(30, opts.TicketMinutes * 4);
        var stuckMinutes = Math.Clamp(opts.StuckScanningMinutes, 1, 24 * 60);
        var raw = await metadata.QueryOpsOrphansAsync(
                TimeSpan.FromMinutes(orphanMinutes),
                TimeSpan.FromMinutes(stuckMinutes),
                sampleLimit: 1,
                cancellationToken)
            .ConfigureAwait(false);

        return new AttachmentOpsHintsDto(
            MetadataAvailable: raw.Available,
            UnavailableReason: raw.UnavailableReason,
            StorageProvider: opts.Provider,
            ActiveAttachmentCount: raw.Available ? raw.ActiveAttachmentCount : null,
            ActiveSizeBytesSum: raw.Available ? raw.ActiveSizeBytesSum : null,
            DownloadTicketMinutes: opts.DownloadTicketMinutes,
            DownloadTicketNote:
            "Ephemeral Redis keys (attachment:download:*); not enumerated — avoid KEYS/SCAN in ops path.",
            RelatedMetricNames: RelatedMetricNames,
            GeneratedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public async Task<bool> RescanAsync(
        long adminUserId,
        string attachmentId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var job = await FindJobAsync(attachmentId, cancellationToken).ConfigureAwait(false);
        if (job is null
            || !metadata.IsAvailable
            || job.Status is AttachmentScanJobStatus.Processing or AttachmentScanJobStatus.Finalizing)
            return false;

        await metadata.MarkUploadedScanningAsync(
                attachmentId, job.UserId, job.SizeBytes, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        job.Status = AttachmentScanJobStatus.Pending;
        job.AttemptCount = 0;
        job.NextAttemptAt = DateTimeOffset.UtcNow;
        job.CompletedAt = null;
        job.LastError = null;
        job.LeaseOwner = null;
        job.LeaseToken = null;
        job.LeaseExpiresAt = null;
        await WriteAuditAsync(
                adminUserId, job, "AttachmentRescan", reason, cancellationToken)
            .ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger?.LogInformation(
            "管理员触发附件重扫 AdminUserId={AdminUserId} AttachmentId={AttachmentId}",
            adminUserId,
            attachmentId);
        return true;
    }

    public async Task<bool> DeleteAsync(
        long adminUserId,
        string attachmentId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var job = await FindJobAsync(attachmentId, cancellationToken).ConfigureAwait(false);
        if (job is null || blobDeletes is null)
            return false;

        await blobDeletes.EnqueueAsync(
                [(job.ObjectKey, job.AttachmentId)], job.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (metadata.IsAvailable)
        {
            await metadata.MarkRejectedAsync(
                    job.AttachmentId, job.UserId, "admin_deleted", cancellationToken)
                .ConfigureAwait(false);
        }

        job.Status = AttachmentScanJobStatus.DeadLetter;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.LastError = "admin_deleted";
        job.LeaseOwner = null;
        job.LeaseToken = null;
        job.LeaseExpiresAt = null;
        await WriteAuditAsync(
                adminUserId, job, "AttachmentDelete", reason, cancellationToken)
            .ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ReleaseAsync(
        long adminUserId,
        string attachmentId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var job = await FindJobAsync(attachmentId, cancellationToken).ConfigureAwait(false);
        if (job is null || !metadata.IsAvailable || storage is not IAttachmentScanStateMarker marker)
            return false;

        await marker.MarkScanStateAsync(
                job.ObjectKey, "confirmed", cancellationToken)
            .ConfigureAwait(false);

        await metadata.ConfirmAsync(
                job.AttachmentId,
                job.UserId,
                job.ObjectKey,
                publicUrl: null,
                contentType: string.IsNullOrWhiteSpace(job.ContentType)
                    ? "application/octet-stream"
                    : job.ContentType,
                sizeBytes: job.SizeBytes,
                originalName: job.OriginalName,
                cancellationToken)
            .ConfigureAwait(false);

        job.Status = AttachmentScanJobStatus.Done;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.LastError = "admin_released";
        job.LeaseOwner = null;
        job.LeaseToken = null;
        job.LeaseExpiresAt = null;
        await WriteAuditAsync(
                adminUserId, job, "AttachmentRelease", reason, cancellationToken)
            .ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private Task<AttachmentScanJob?> FindJobAsync(
        string attachmentId,
        CancellationToken cancellationToken)
        => db.AttachmentScanJobs
            .Where(x => x.AttachmentId == attachmentId)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task WriteAuditAsync(
        long adminUserId,
        AttachmentScanJob job,
        string action,
        string? reason,
        CancellationToken cancellationToken)
    {
        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            AdminUserId = adminUserId,
            TargetUserId = job.UserId,
            Action = action,
            Reason = string.IsNullOrWhiteSpace(reason) ? "manual_attachment_ops" : reason.Trim(),
            Detail = $"AttachmentId={job.AttachmentId};ObjectKey={job.ObjectKey}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static long AgeMs(long nowMs, long? atMs) =>
        atMs is { } at ? Math.Max(0, nowMs - at) : 0;

    private static IReadOnlyList<AttachmentOpsSampleRowDto> MapSamples(
        IReadOnlyList<AttachmentOpsOrphanSample> samples,
        long nowMs)
    {
        return samples.Select(x =>
        {
            var statusName = Enum.IsDefined(typeof(AttachmentStatus), x.Status)
                ? ((AttachmentStatus)x.Status).ToString()
                : x.Status.ToString();
            return new AttachmentOpsSampleRowDto(
                x.AttachmentId,
                x.ObjectKey,
                x.UploaderUserId,
                statusName,
                x.Status,
                x.SizeBytes,
                x.CreatedAtMs,
                Math.Max(0, nowMs - x.CreatedAtMs));
        }).ToList();
    }
}
