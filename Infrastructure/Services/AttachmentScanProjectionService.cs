using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
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

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var batchSize = Math.Clamp(options.Value.ScanBatchSize, 1, 200);
        var claimed = await ClaimDueAsync(batchSize, cancellationToken).ConfigureAwait(false);
        var completed = 0;
        foreach (var projection in claimed)
        {
            if (await ProcessClaimedAsync(projection, cancellationToken).ConfigureAwait(false))
                completed++;
        }

        return completed;
    }

    private async Task<IReadOnlyList<AttachmentScanProjection>> ClaimDueAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(Lease);
        var owner = $"{ProcessOwner}:{Guid.NewGuid():N}";
        if (owner.Length > 128)
            owner = owner[..128];
        var leaseToken = Guid.NewGuid().ToString("N");

        if (IsNpgsql())
        {
            var ids = await ClaimDueIdsNpgsqlAsync(
                    batchSize, owner, leaseToken, now, leaseUntil, cancellationToken)
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
            projection.LeaseToken = leaseToken;
            projection.LeaseExpiresAt = leaseUntil;
        }

        if (due.Count > 0)
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return due;
    }

    private async Task<bool> ProcessClaimedAsync(
        AttachmentScanProjection claimed,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!metadata.IsAvailable)
                throw new InvalidOperationException(metadata.UnavailableReason);

            var contentType = string.IsNullOrWhiteSpace(claimed.ContentType)
                ? "application/octet-stream"
                : claimed.ContentType;

            // This updates the full-stream SHA-256 without changing an already-final
            // metadata state on an at-least-once retry.
            if (!string.IsNullOrWhiteSpace(claimed.ContentHash))
            {
                await metadata.MarkUploadedScanningAsync(
                        claimed.AttachmentId,
                        claimed.UserId,
                        claimed.SizeBytes,
                        claimed.ContentHash,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

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

                await metadata.ConfirmAsync(
                        claimed.AttachmentId,
                        claimed.UserId,
                        confirmedObjectKey,
                        publicUrl: null,
                        contentType,
                        claimed.SizeBytes,
                        claimed.OriginalName,
                        cancellationToken)
                    .ConfigureAwait(false);

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
                if (storage is IAttachmentScanStateMarker marker)
                {
                    await marker.MarkScanStateAsync(
                            claimed.ObjectKey, "rejected", cancellationToken)
                        .ConfigureAwait(false);
                }

                await metadata.MarkRejectedAsync(
                        claimed.AttachmentId,
                        claimed.UserId,
                        claimed.RejectionReason,
                        cancellationToken)
                    .ConfigureAwait(false);

                await blobDeletes.EnqueueAsync(
                        [(claimed.ObjectKey, claimed.AttachmentId)],
                        claimed.UserId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var completed = await CompleteFencedAsync(claimed, cancellationToken)
                .ConfigureAwait(false);
            if (completed)
            {
                AuthSecurityMetrics.AttachmentScan(
                    claimed.Outcome == AttachmentScanProjectionOutcome.Confirmed
                        ? "confirmed"
                        : "rejected");
            }
            return completed;
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

    private async Task<List<long>> ClaimDueIdsNpgsqlAsync(
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
            UPDATE "T_AttachmentScanProjection" AS p
            SET "Status" = 'Processing',
                "LeaseOwner" = @owner,
                "LeaseToken" = @lease_token,
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
        AddParameter(command, "lease_token", leaseToken);
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
            : null;

        if (!IsNpgsql())
        {
            var tracked = await db.AttachmentScanProjections
                .FirstOrDefaultAsync(x => x.Id == claimed.Id, cancellationToken)
                .ConfigureAwait(false);
            if (!OwnsLease(tracked, claimed))
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
                            && x.LeaseToken == claimed.LeaseToken)
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

    private async Task<bool> RetryOrDeadLetterAsync(
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
            if (!OwnsLease(tracked, claimed))
                return false;

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
            return dead;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var projectionRows = await db.AttachmentScanProjections
                .Where(x => x.Id == claimed.Id
                            && x.Status == AttachmentScanProjectionStatus.Processing
                            && x.LeaseOwner == claimed.LeaseOwner
                            && x.LeaseToken == claimed.LeaseToken)
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
                return false;
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
                return false;
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
            return dead;
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

    private TimeSpan ComputeBackoff(int attemptCount)
    {
        var baseSeconds = Math.Max(5, options.Value.ScanBackoffSeconds);
        var exp = Math.Min(attemptCount - 1, 10);
        var seconds = Math.Min(3600, baseSeconds * Math.Pow(2, Math.Max(0, exp)));
        return TimeSpan.FromSeconds(seconds);
    }

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
