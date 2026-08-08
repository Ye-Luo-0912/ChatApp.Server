using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Diagnostics;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Projects a scan verdict that was already fenced and committed in the local
/// database. This is deliberately separate from <see cref="AttachmentScanService"/>:
/// a scanner that loses its lease can no longer update Realtime metadata directly.
/// </summary>
public sealed class AttachmentScanProjectionService(
    UserDbContext db,
    IAttachmentMetadataStore metadata,
    IAttachmentStorage storage,
    IAttachmentBlobDeleteService blobDeletes,
    IOptions<AttachmentStorageOptions> options,
    ILogger<AttachmentScanProjectionService> logger) : IAttachmentScanProjectionService
{
    private static readonly string ProcessOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:attachment-projection";

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var batchSize = Math.Clamp(options.Value.ScanBatchSize, 1, 200);
        var claimed = await ClaimDueAsync(batchSize, cancellationToken).ConfigureAwait(false);
        var completed = 0;
        foreach (var projection in claimed)
        {
            var result = await ProcessClaimedAsync(projection, cancellationToken).ConfigureAwait(false);
            if (result is AttachmentScanProjectionProcessResult.Completed
                or AttachmentScanProjectionProcessResult.DeadLetter)
                completed++;
        }

        return completed;
    }

    public async Task<IReadOnlyList<AttachmentScanProjection>> ClaimDueAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        batchSize = Math.Clamp(batchSize, 1, 200);
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.AddSeconds(
            Math.Clamp(options.Value.ProjectionLeaseSeconds, 30, 900));
        var owner = $"{ProcessOwner}:{Guid.NewGuid():N}";
        if (owner.Length > 128)
            owner = owner[..128];

        if (IsNpgsql())
        {
            var ids = await ClaimDueIdsNpgsqlAsync(
                    batchSize, owner, now, leaseUntil, cancellationToken)
                .ConfigureAwait(false);
            if (ids.Count == 0)
                return [];

            return await db.AttachmentScanProjections
                .AsNoTracking()
                .Where(x => ids.Contains(x.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var due = await db.AttachmentScanProjections
            .Where(x => (x.Status == AttachmentScanProjectionStatus.Pending
                         && x.NextAttemptAt <= now)
                        || (x.Status == AttachmentScanProjectionStatus.Processing
                            && x.LeaseExpiresAt != null
                            && x.LeaseExpiresAt < now))
            .OrderBy(x => x.NextAttemptAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var projection in due)
        {
            projection.Status = AttachmentScanProjectionStatus.Processing;
            projection.LeaseOwner = owner;
            projection.LeaseToken = Guid.NewGuid().ToString("N");
            projection.LeaseExpiresAt = leaseUntil;
        }

        if (due.Count > 0)
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return due;
    }

    public async Task<AttachmentScanProjectionProcessResult> ProcessClaimedAsync(
        AttachmentScanProjection claimed,
        CancellationToken cancellationToken)
    {
        if (!OwnsLease(claimed, claimed))
            return AttachmentScanProjectionProcessResult.LeaseLost;

        try
        {
            await ExecuteClaimedAsync(claimed, cancellationToken).ConfigureAwait(false);

            var completed = await CompleteFencedAsync(claimed, cancellationToken)
                .ConfigureAwait(false);
            if (completed)
            {
                AuthSecurityMetrics.AttachmentScan(
                    claimed.Outcome == AttachmentScanProjectionOutcome.Confirmed
                        ? "confirmed"
                        : claimed.Outcome == AttachmentScanProjectionOutcome.Abandoned
                            ? "abandoned"
                        : "rejected");
            }
            return completed
                ? AttachmentScanProjectionProcessResult.Completed
                : AttachmentScanProjectionProcessResult.LeaseLost;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "附件扫描结果投递失败 ProjectionId={ProjectionId} AttachmentId={AttachmentId}",
                claimed.Id,
                claimed.AttachmentId);
            return await RetryOrDeadLetterAsync(claimed, ex.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes only the external metadata/storage portion of a projection.
    /// The shared leased-job executor performs the fenced terminal update after
    /// this method returns, so a long external operation never holds a stale
    /// projection row in a second DbContext.
    /// </summary>
    public async Task ExecuteClaimedAsync(
        AttachmentScanProjection claimed,
        CancellationToken cancellationToken = default)
    {
        if (!OwnsLease(claimed, claimed))
            throw new InvalidOperationException("附件扫描投影租约无效");

        // ClaimDueAsync returns a detached snapshot for PostgreSQL. Re-read the
        // row before touching Realtime/S3 so account deletion can fence an
        // already-claimed projection by advancing its deletion epoch. The
        // owner/token check also prevents a stale worker from performing an
        // external write after the row was reclaimed.
        var currentProjection = await db.AttachmentScanProjections
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == claimed.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!OwnsLease(currentProjection, claimed))
            throw new InvalidOperationException("附件扫描投影租约已丢失");

        if (currentProjection!.UploaderDeletionEpoch != claimed.UploaderDeletionEpoch)
        {
            claimed.Outcome = AttachmentScanProjectionOutcome.Abandoned;
            await AbandonExternalAsync(claimed, cancellationToken).ConfigureAwait(false);
            return;
        }

        var uploader = await db.Users.AsNoTracking()
            .Where(x => x.Id == claimed.UserId)
            .Select(x => new { x.DeletionEpoch, x.DeletionScheduledAt })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        // Legacy/test rows can exist without an AspNetUsers row and carry epoch
        // 0. Production upload/confirm paths always persist the uploader epoch;
        // a missing user is therefore a deletion fence only for non-zero epochs.
        if ((uploader is null && claimed.UploaderDeletionEpoch > 0)
            || uploader?.DeletionScheduledAt is not null
            || (uploader is not null
                && uploader.DeletionEpoch != claimed.UploaderDeletionEpoch))
        {
            await AbandonExternalAsync(claimed, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!metadata.IsAvailable)
            throw new InvalidOperationException(metadata.UnavailableReason);

        var contentType = string.IsNullOrWhiteSpace(claimed.ContentType)
            ? "application/octet-stream"
            : claimed.ContentType;

        // This updates the full-stream SHA-256 without changing an already-final
        // metadata state on an at-least-once retry. The optional projection
        // store adds a target-side ProjectionId/ScanVersion CAS fence.
        var scanState = await MarkUploadedScanningAsync(claimed, cancellationToken)
            .ConfigureAwait(false);
        if (scanState == AttachmentProjectionWriteResult.AlreadySuperseded)
        {
            await EnqueueKnownObjectKeysAsync(claimed, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (scanState != AttachmentProjectionWriteResult.Applied)
            throw new InvalidOperationException("附件元数据不存在，无法投影扫描状态");

        if (!await IsLeaseStillOwnedAsync(claimed, cancellationToken)
                .ConfigureAwait(false))
            throw new InvalidOperationException("附件扫描投影租约已丢失");

        if (claimed.Outcome == AttachmentScanProjectionOutcome.Confirmed)
        {
            var confirmedObjectKey = claimed.ObjectKey;
            string? stagingObjectKeyToDelete = null;
            if (storage is IAttachmentScanFinalizer finalizer)
            {
                var finalized = await finalizer.FinalizeConfirmedAsync(
                        claimed.AttachmentId,
                        claimed.UserId,
                        claimed.ObjectKey,
                        claimed.SourceEntityTag,
                        cancellationToken)
                    .ConfigureAwait(false);
                confirmedObjectKey = finalized.ObjectKey;
                stagingObjectKeyToDelete = finalized.StagingObjectKeyToDelete;
            }
            else if (storage is IAttachmentScanStateMarker marker)
            {
                await marker.MarkScanStateAsync(
                        claimed.ObjectKey, "confirmed", cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!await IsLeaseStillOwnedAsync(claimed, cancellationToken)
                    .ConfigureAwait(false))
                throw new InvalidOperationException("附件扫描投影租约已丢失");

            var confirmed = await ConfirmAsync(
                    claimed,
                    confirmedObjectKey,
                    contentType,
                    cancellationToken)
                .ConfigureAwait(false);
            if (confirmed == AttachmentProjectionWriteResult.AlreadySuperseded)
            {
                await EnqueueKnownObjectKeysAsync(claimed, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (confirmed != AttachmentProjectionWriteResult.Applied)
                throw new InvalidOperationException("附件元数据不存在，无法投影确认状态");

            if (!string.IsNullOrWhiteSpace(stagingObjectKeyToDelete))
            {
                await blobDeletes.EnqueueAsync(
                        [(stagingObjectKeyToDelete, claimed.AttachmentId)],
                        claimed.UserId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            if (!await IsLeaseStillOwnedAsync(claimed, cancellationToken)
                    .ConfigureAwait(false))
                throw new InvalidOperationException("附件扫描投影租约已丢失");

            if (storage is IAttachmentScanStateMarker marker)
            {
                await marker.MarkScanStateAsync(
                        claimed.ObjectKey, "rejected", cancellationToken)
                    .ConfigureAwait(false);
            }

            var rejected = await MarkRejectedAsync(claimed, cancellationToken)
                .ConfigureAwait(false);
            if (rejected == AttachmentProjectionWriteResult.AlreadySuperseded)
            {
                await EnqueueKnownObjectKeysAsync(claimed, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (rejected != AttachmentProjectionWriteResult.Applied)
                throw new InvalidOperationException("附件元数据不存在，无法投影拒绝状态");

            await blobDeletes.EnqueueAsync(
                    [(claimed.ObjectKey, claimed.AttachmentId)],
                    claimed.UserId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<LeaseRenewalResult> RenewLeaseAsync(
        long projectionId,
        string leaseOwner,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(
            Math.Clamp(options.Value.ProjectionLeaseSeconds, 30, 900));
        try
        {
            if (!IsNpgsql())
            {
                var tracked = await db.AttachmentScanProjections
                    .FirstOrDefaultAsync(
                        x => x.Id == projectionId
                             && x.Status == AttachmentScanProjectionStatus.Processing
                             && x.LeaseOwner == leaseOwner
                             && x.LeaseToken == leaseToken,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (tracked is null)
                    return LeaseRenewalResult.LeaseLost;

                tracked.LeaseExpiresAt = until;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return LeaseRenewalResult.Renewed;
            }

            var updated = await db.AttachmentScanProjections
                .Where(x => x.Id == projectionId
                    && x.Status == AttachmentScanProjectionStatus.Processing
                    && x.LeaseOwner == leaseOwner
                    && x.LeaseToken == leaseToken)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.LeaseExpiresAt, until),
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
        catch (Exception ex)
        {
            logger.LogDebug(ex, "附件扫描投影租约续租失败 ProjectionId={ProjectionId}", projectionId);
            return LeaseRenewalResult.TransientFailure;
        }
    }

    /// <summary>Shared executor 的 fenced terminal hooks。</summary>
    public async Task<bool> CompleteClaimedAsync(
        AttachmentScanProjection claimed,
        CancellationToken cancellationToken = default)
    {
        var completed = await CompleteFencedAsync(claimed, cancellationToken).ConfigureAwait(false);
        if (completed)
        {
            AuthSecurityMetrics.AttachmentScan(
                claimed.Outcome == AttachmentScanProjectionOutcome.Confirmed
                    ? "confirmed"
                    : claimed.Outcome == AttachmentScanProjectionOutcome.Abandoned
                        ? "abandoned"
                        : "rejected");
        }

        return completed;
    }

    public async Task<bool> RetryClaimedAsync(
        AttachmentScanProjection claimed,
        string error,
        CancellationToken cancellationToken = default)
        => await RetryOrDeadLetterAsync(claimed, error, cancellationToken)
            .ConfigureAwait(false) != AttachmentScanProjectionProcessResult.LeaseLost;

    public async Task<bool> DeadLetterClaimedAsync(
        AttachmentScanProjection claimed,
        string error,
        CancellationToken cancellationToken = default)
        => await RetryOrDeadLetterAsync(claimed, error, cancellationToken)
            .ConfigureAwait(false) != AttachmentScanProjectionProcessResult.LeaseLost;

    private async Task<AttachmentProjectionWriteResult> MarkUploadedScanningAsync(
        AttachmentScanProjection projection,
        CancellationToken cancellationToken)
    {
        if (metadata is IAttachmentScanProjectionMetadataStore fenced)
        {
            return await fenced.MarkUploadedScanningAsync(
                    projection.AttachmentId,
                    projection.UserId,
                    projection.SizeBytes,
                    projection.Id,
                    projection.ScanVersion,
                    projection.ContentHash,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await metadata.MarkUploadedScanningAsync(
                projection.AttachmentId,
                projection.UserId,
                projection.SizeBytes,
                projection.ContentHash,
                cancellationToken)
            .ConfigureAwait(false);
        return AttachmentProjectionWriteResult.Applied;
    }

    private async Task<AttachmentProjectionWriteResult> ConfirmAsync(
        AttachmentScanProjection projection,
        string objectKey,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (metadata is IAttachmentScanProjectionMetadataStore fenced)
        {
            return await fenced.ConfirmAsync(
                    projection.AttachmentId,
                    projection.UserId,
                    objectKey,
                    publicUrl: null,
                    contentType,
                    projection.SizeBytes,
                    projection.OriginalName,
                    projection.Id,
                    projection.ScanVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await metadata.ConfirmAsync(
                projection.AttachmentId,
                projection.UserId,
                objectKey,
                publicUrl: null,
                contentType,
                projection.SizeBytes,
                projection.OriginalName,
                cancellationToken)
            .ConfigureAwait(false);
        return AttachmentProjectionWriteResult.Applied;
    }

    private async Task<AttachmentProjectionWriteResult> MarkRejectedAsync(
        AttachmentScanProjection projection,
        CancellationToken cancellationToken)
    {
        if (metadata is IAttachmentScanProjectionMetadataStore fenced)
        {
            return await fenced.MarkRejectedAsync(
                    projection.AttachmentId,
                    projection.UserId,
                    projection.RejectionReason,
                    projection.Id,
                    projection.ScanVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await metadata.MarkRejectedAsync(
                projection.AttachmentId,
                projection.UserId,
                projection.RejectionReason,
                cancellationToken)
            .ConfigureAwait(false);
        return AttachmentProjectionWriteResult.Applied;
    }

    private async Task AbandonExternalAsync(
        AttachmentScanProjection claimed,
        CancellationToken cancellationToken)
    {
        if (!metadata.IsAvailable)
            throw new InvalidOperationException(metadata.UnavailableReason);

        AttachmentProjectionWriteResult result;
        if (metadata is IAttachmentScanProjectionMetadataStore fenced)
        {
            result = await fenced.MarkAbandonedAsync(
                    claimed.AttachmentId,
                    claimed.UserId,
                    claimed.Id,
                    claimed.ScanVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await metadata.MarkAbandonedAsync([claimed.AttachmentId], cancellationToken)
                .ConfigureAwait(false);
            result = AttachmentProjectionWriteResult.Applied;
        }

        if (result == AttachmentProjectionWriteResult.NotFound)
            throw new InvalidOperationException("附件元数据不存在，无法放弃扫描投影");

        // AlreadySuperseded means an account-deletion/abandon fence won the
        // target-side CAS. The projection still owns a known object key that
        // must enter the durable blob-delete path.
        if (result is AttachmentProjectionWriteResult.Applied
            or AttachmentProjectionWriteResult.AlreadySuperseded)
            await EnqueueKnownObjectKeysAsync(claimed, cancellationToken).ConfigureAwait(false);

        claimed.Outcome = AttachmentScanProjectionOutcome.Abandoned;
    }

    private Task EnqueueKnownObjectKeysAsync(
        AttachmentScanProjection claimed,
        CancellationToken cancellationToken)
        => blobDeletes.EnqueueAsync(
            KnownObjectKeys(claimed),
            claimed.UserId,
            cancellationToken);

    private static IReadOnlyList<(string ObjectKey, string? AttachmentId)> KnownObjectKeys(
        AttachmentScanProjection projection)
    {
        var keys = new List<(string, string?)>(2);
        if (!string.IsNullOrWhiteSpace(projection.ObjectKey))
            keys.Add((projection.ObjectKey, projection.AttachmentId));

        // S3 promotion is deliberately deterministic. Enqueuing this key is
        // harmless for Local storage and covers an ambiguous copy that completed
        // immediately before a target-side CAS rejected the projection.
        var confirmed = $"attachments/{projection.UserId}/confirmed/{projection.AttachmentId}";
        if (!string.Equals(confirmed, projection.ObjectKey, StringComparison.Ordinal))
            keys.Add((confirmed, projection.AttachmentId));
        return keys;
    }

    private async Task<List<long>> ClaimDueIdsNpgsqlAsync(
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
            UPDATE "T_AttachmentScanProjection" AS p
            SET "Status" = 'Processing',
                "LeaseOwner" = @owner,
                "LeaseToken" = md5(random()::text || clock_timestamp()::text || p."Id"::text),
                "LeaseExpiresAt" = @lease_until
            WHERE p."Id" IN (
                SELECT c."Id"
                FROM "T_AttachmentScanProjection" AS c
                WHERE (c."Status" = 'Pending' AND c."NextAttemptAt" <= @now)
                   OR (c."Status" = 'Processing'
                       AND c."LeaseExpiresAt" IS NOT NULL
                       AND c."LeaseExpiresAt" < @now)
                ORDER BY c."NextAttemptAt"
                FOR UPDATE SKIP LOCKED
                LIMIT @batch
            )
            RETURNING p."Id";
            """;

        AddParameter(command, "owner", owner);
        AddParameter(command, "lease_until", leaseUntil);
        AddParameter(command, "now", now);
        AddParameter(command, "batch", batchSize);

        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    private async Task<bool> CompleteFencedAsync(
        AttachmentScanProjection claimed,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var scanError = claimed.Outcome == AttachmentScanProjectionOutcome.Rejected
            ? Truncate(claimed.RejectionReason ?? "rejected", 500)
            : claimed.Outcome == AttachmentScanProjectionOutcome.Abandoned
                ? "account_deletion_epoch_changed"
                : null;

        if (!IsNpgsql())
        {
            var tracked = await db.AttachmentScanProjections
                .FirstOrDefaultAsync(x => x.Id == claimed.Id, cancellationToken)
                .ConfigureAwait(false);
            if (!OwnsLease(tracked, claimed)
                || tracked!.UploaderDeletionEpoch != claimed.UploaderDeletionEpoch)
                return false;

            var job = await db.AttachmentScanJobs
                .FirstOrDefaultAsync(x => x.Id == claimed.ScanJobId, cancellationToken)
                .ConfigureAwait(false);
            if (job is null || job.Status != AttachmentScanJobStatus.Finalizing)
                return false;

            tracked!.Status = AttachmentScanProjectionStatus.Done;
            tracked.CompletedAt = now;
            tracked.LastError = null;
            tracked.LeaseOwner = null;
            tracked.LeaseToken = null;
            tracked.LeaseExpiresAt = null;
            job.Status = AttachmentScanJobStatus.Done;
            job.CompletedAt = now;
            job.LastError = scanError;
            job.LeaseOwner = null;
            job.LeaseToken = null;
            job.LeaseExpiresAt = null;

            var saga = await db.AttachmentConfirmSagas
                .FirstOrDefaultAsync(
                    x => x.AttachmentId == claimed.AttachmentId
                         && x.UserId == claimed.UserId
                         && x.Status == AttachmentConfirmSagaStatus.ScanQueued,
                    cancellationToken)
                .ConfigureAwait(false);
            if (saga is not null)
            {
                saga.Status = AttachmentConfirmSagaStatus.Completed;
                saga.CompletedAt = now;
                saga.UpdatedAt = now;
                saga.LastError = null;
                saga.LeaseOwner = null;
                saga.LeaseToken = null;
                saga.LeaseExpiresAt = null;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            AuthSecurityMetrics.AttachmentPendingScanDelta(-1);
            return true;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var projectionRows = await db.AttachmentScanProjections
                .Where(x => x.Id == claimed.Id
                            && x.Status == AttachmentScanProjectionStatus.Processing
                            && x.LeaseOwner == claimed.LeaseOwner
                            && x.LeaseToken == claimed.LeaseToken
                            && x.UploaderDeletionEpoch == claimed.UploaderDeletionEpoch)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, AttachmentScanProjectionStatus.Done)
                    .SetProperty(x => x.CompletedAt, now)
                    .SetProperty(x => x.LastError, (string?)null)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null),
                    cancellationToken)
                .ConfigureAwait(false);
            if (projectionRows != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            var jobRows = await db.AttachmentScanJobs
                .Where(x => x.Id == claimed.ScanJobId
                            && x.Status == AttachmentScanJobStatus.Finalizing)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, AttachmentScanJobStatus.Done)
                    .SetProperty(x => x.CompletedAt, now)
                    .SetProperty(x => x.LastError, scanError)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null),
                    cancellationToken)
                .ConfigureAwait(false);
            if (jobRows != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            // The projection and its confirm saga share the Server DB. Complete
            // both in this transaction so a successful projection cannot leave
            // the client-visible saga stuck in ScanQueued.
            await db.AttachmentConfirmSagas
                .Where(x => x.AttachmentId == claimed.AttachmentId
                            && x.UserId == claimed.UserId
                            && x.Status == AttachmentConfirmSagaStatus.ScanQueued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, AttachmentConfirmSagaStatus.Completed)
                    .SetProperty(x => x.CompletedAt, now)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.LastError, (string?)null)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null),
                    cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            AuthSecurityMetrics.AttachmentPendingScanDelta(-1);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<AttachmentScanProjectionProcessResult> RetryOrDeadLetterAsync(
        AttachmentScanProjection claimed,
        string error,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var attempts = Math.Max(1, claimed.AttemptCount + 1);
        var dead = attempts >= Math.Max(1, options.Value.MaxScanAttempts);
        var nextAttempt = dead ? claimed.NextAttemptAt : now.Add(ComputeBackoff(attempts));
        var lastError = Truncate(error, 500);

        if (!IsNpgsql())
        {
            var tracked = await db.AttachmentScanProjections
                .FirstOrDefaultAsync(x => x.Id == claimed.Id, cancellationToken)
                .ConfigureAwait(false);
            if (!OwnsLease(tracked, claimed)
                || tracked!.UploaderDeletionEpoch != claimed.UploaderDeletionEpoch)
                return AttachmentScanProjectionProcessResult.LeaseLost;

            tracked!.Status = dead
                ? AttachmentScanProjectionStatus.DeadLetter
                : AttachmentScanProjectionStatus.Pending;
            tracked.AttemptCount = attempts;
            tracked.NextAttemptAt = nextAttempt;
            tracked.LastError = lastError;
            tracked.CompletedAt = dead ? now : null;
            tracked.LeaseOwner = null;
            tracked.LeaseToken = null;
            tracked.LeaseExpiresAt = null;

            var job = await db.AttachmentScanJobs
                .FirstOrDefaultAsync(x => x.Id == claimed.ScanJobId, cancellationToken)
                .ConfigureAwait(false);
            if (job is not null && job.Status == AttachmentScanJobStatus.Finalizing)
            {
                job.LastError = lastError;
                job.NextAttemptAt = nextAttempt;
                if (dead)
                {
                    job.Status = AttachmentScanJobStatus.DeadLetter;
                    job.CompletedAt = now;
                    AuthSecurityMetrics.AttachmentPendingScanDelta(-1);
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (dead)
                AuthSecurityMetrics.AttachmentScan("projection_dead_letter");
            else
                AuthSecurityMetrics.AttachmentScan("projection_retry");
            return dead
                ? AttachmentScanProjectionProcessResult.DeadLetter
                : AttachmentScanProjectionProcessResult.RetryScheduled;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var projectionRows = await db.AttachmentScanProjections
                .Where(x => x.Id == claimed.Id
                            && x.Status == AttachmentScanProjectionStatus.Processing
                            && x.LeaseOwner == claimed.LeaseOwner
                            && x.LeaseToken == claimed.LeaseToken
                            && x.UploaderDeletionEpoch == claimed.UploaderDeletionEpoch)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, dead
                        ? AttachmentScanProjectionStatus.DeadLetter
                        : AttachmentScanProjectionStatus.Pending)
                    .SetProperty(x => x.AttemptCount, attempts)
                    .SetProperty(x => x.NextAttemptAt, nextAttempt)
                    .SetProperty(x => x.LastError, lastError)
                    .SetProperty(x => x.CompletedAt, dead ? now : null)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null),
                    cancellationToken)
                .ConfigureAwait(false);
            if (projectionRows != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return AttachmentScanProjectionProcessResult.LeaseLost;
            }

            var jobRows = await db.AttachmentScanJobs
                .Where(x => x.Id == claimed.ScanJobId
                            && x.Status == AttachmentScanJobStatus.Finalizing)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.LastError, lastError)
                    .SetProperty(x => x.NextAttemptAt, nextAttempt)
                    .SetProperty(x => x.Status, dead
                        ? AttachmentScanJobStatus.DeadLetter
                        : AttachmentScanJobStatus.Finalizing)
                    .SetProperty(x => x.CompletedAt, dead ? now : null),
                    cancellationToken)
                .ConfigureAwait(false);
            if (jobRows != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return AttachmentScanProjectionProcessResult.LeaseLost;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (dead)
            {
                AuthSecurityMetrics.AttachmentPendingScanDelta(-1);
                AuthSecurityMetrics.AttachmentScan("projection_dead_letter");
            }
            else
            {
                AuthSecurityMetrics.AttachmentScan("projection_retry");
            }
            return dead
                ? AttachmentScanProjectionProcessResult.DeadLetter
                : AttachmentScanProjectionProcessResult.RetryScheduled;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private bool IsNpgsql() =>
        db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    private static bool OwnsLease(
        AttachmentScanProjection? current,
        AttachmentScanProjection claimed) =>
        current is not null
        && current.Status == AttachmentScanProjectionStatus.Processing
        && current.LeaseOwner == claimed.LeaseOwner
        && current.LeaseToken == claimed.LeaseToken;

    private Task<bool> IsLeaseStillOwnedAsync(
        AttachmentScanProjection claimed,
        CancellationToken cancellationToken)
        => db.AttachmentScanProjections.AsNoTracking().AnyAsync(
            x => x.Id == claimed.Id
                 && x.Status == AttachmentScanProjectionStatus.Processing
                 && x.LeaseOwner == claimed.LeaseOwner
                 && x.LeaseToken == claimed.LeaseToken
                 && x.UploaderDeletionEpoch == claimed.UploaderDeletionEpoch,
            cancellationToken);

    private TimeSpan ComputeBackoff(int attemptCount)
        => LeasedJobBackoff.ExponentialWithJitter(
            TimeSpan.FromSeconds(Math.Max(5, options.Value.ScanBackoffSeconds)),
            attemptCount,
            TimeSpan.FromHours(1));

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static void AddParameter(
        System.Data.Common.DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
