using ChatApp.Realtime.Abstractions.Stores;
using Core.Interfaces;
using Core.Models.Email;
using Core.Models.Export;
using Core.Models.Notifications;
using Core.Models.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>
/// Unified DLQ facade. It intentionally performs narrow, status-fenced
/// updates per queue; it does not bypass a domain worker or expose lease
/// tokens to operators.
/// </summary>
public sealed class DeadLetterAdminService(
    UserDbContext db,
    IAccountCleanupSagaService accountCleanupSaga) : IDeadLetterAdminService
{
    public async Task<DeadLetterPage> ListAsync(
        string? queue = null,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 200);
        if (!string.IsNullOrWhiteSpace(queue) && !DeadLetterQueueNames.All.Contains(queue.Trim(), StringComparer.Ordinal))
            return new DeadLetterPage([], queue, offset, limit, false);

        var rows = new List<DeadLetterItemDto>(limit * 2);
        foreach (var name in string.IsNullOrWhiteSpace(queue)
                     ? DeadLetterQueueNames.All
                     : [queue.Trim()])
        {
            await AppendQueueAsync(rows, name, limit, cancellationToken).ConfigureAwait(false);
        }

        rows.Sort(static (a, b) =>
        {
            var left = a.CreatedAt ?? DateTimeOffset.MinValue;
            var right = b.CreatedAt ?? DateTimeOffset.MinValue;
            var byDate = right.CompareTo(left);
            return byDate != 0
                ? byDate
                : string.CompareOrdinal(a.Queue + ":" + a.JobId, b.Queue + ":" + b.JobId);
        });

        var page = rows.Skip(offset).Take(limit + 1).ToList();
        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);
        await AttachResolutionsAsync(page, cancellationToken).ConfigureAwait(false);
        return new DeadLetterPage(page, queue, offset, limit, hasMore);
    }

    public async Task<DeadLetterItemDto?> GetAsync(
        string queue,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        queue = queue?.Trim() ?? string.Empty;
        if (!DeadLetterQueueNames.All.Contains(queue, StringComparer.Ordinal)
            || string.IsNullOrWhiteSpace(jobId))
            return null;

        var rows = new List<DeadLetterItemDto>(1);
        await AppendQueueAsync(rows, queue, 1, cancellationToken, jobId.Trim())
            .ConfigureAwait(false);
        if (rows.Count == 0)
            return null;
        await AttachResolutionsAsync(rows, cancellationToken).ConfigureAwait(false);
        return rows[0];
    }

    public Task<DeadLetterActionResult> ReplayAsync(
        long adminUserId,
        string queue,
        string jobId,
        string? reason,
        CancellationToken cancellationToken = default)
        => TransitionAsync(adminUserId, queue, jobId, "replay", reason, cancellationToken);

    public Task<DeadLetterActionResult> RepairAsync(
        long adminUserId,
        string queue,
        string jobId,
        string? reason,
        CancellationToken cancellationToken = default)
        => TransitionAsync(adminUserId, queue, jobId, "repair", reason, cancellationToken);

    public async Task<DeadLetterActionResult> SkipAsync(
        long adminUserId,
        string queue,
        string jobId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var item = await GetAsync(queue, jobId, cancellationToken).ConfigureAwait(false);
        if (item is null)
            return Missing(queue, jobId);

        var alreadySkipped = await db.JobDeadLetterResolutions.AnyAsync(
                x => x.Queue == queue && x.JobId == jobId && x.Action == "skip",
                cancellationToken)
            .ConfigureAwait(false);
        if (!alreadySkipped)
        {
            db.JobDeadLetterResolutions.Add(new JobDeadLetterResolution
            {
                Queue = queue,
                JobId = jobId,
                Action = "skip",
                AdminUserId = adminUserId,
                Reason = NormalizeReason(reason),
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var skipped = item with { ResolutionAction = "skip", ResolutionAt = DateTimeOffset.UtcNow };
        return new DeadLetterActionResult(true, "skipped", "已标记为跳过", skipped);
    }

    private async Task<DeadLetterActionResult> TransitionAsync(
        long adminUserId,
        string queue,
        string jobId,
        string action,
        string? reason,
        CancellationToken cancellationToken)
    {
        queue = queue?.Trim() ?? string.Empty;
        jobId = jobId?.Trim() ?? string.Empty;
        var item = await GetAsync(queue, jobId, cancellationToken).ConfigureAwait(false);
        if (item is null)
            return Missing(queue, jobId);

        var updated = await ResetQueueAsync(queue, jobId, cancellationToken).ConfigureAwait(false);
        if (!updated)
            return new DeadLetterActionResult(false, "not_dead", "作业状态已变化，未执行操作", item);

        db.JobDeadLetterResolutions.Add(new JobDeadLetterResolution
        {
            Queue = queue,
            JobId = jobId,
            Action = action,
            AdminUserId = adminUserId,
            Reason = NormalizeReason(reason),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var after = await GetAsync(queue, jobId, cancellationToken).ConfigureAwait(false);
        return new DeadLetterActionResult(
            true,
            action == "repair" ? "repaired" : "replayed",
            action == "repair" ? "已修复并重新入队" : "已重新入队",
            after ?? item);
    }

    private async Task<bool> ResetQueueAsync(
        string queue,
        string jobId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var numericQueue = queue is not DeadLetterQueueNames.RealtimeOutbox
            and not DeadLetterQueueNames.DataExport
            and not DeadLetterQueueNames.DataExportBlobDelete;
        var parsedId = long.TryParse(jobId, out var id);
        if (numericQueue && !parsedId)
            return false;

        return queue switch
        {
            DeadLetterQueueNames.EmailOutbox => await db.EmailOutbox
                .Where(x => x.Id == id && x.Status == EmailOutboxStatus.Dead)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, EmailOutboxStatus.Pending)
                    .SetProperty(x => x.NextAttemptAt, now.UtcDateTime)
                    .SetProperty(x => x.LockedAt, (DateTime?)null)
                    .SetProperty(x => x.LockOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LastError, (string?)null)
                    .SetProperty(x => x.UpdatedAt, now.UtcDateTime), cancellationToken) == 1,
            DeadLetterQueueNames.NotificationOutbox => await db.NotificationOutbox
                .Where(x => x.Id == id && x.Status == NotificationOutboxStatus.Dead)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, NotificationOutboxStatus.Pending)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.LockedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.LockOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LastError, (string?)null)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken) == 1,
            DeadLetterQueueNames.AttachmentScan => await db.AttachmentScanJobs
                .Where(x => x.Id == id && x.Status == AttachmentScanJobStatus.DeadLetter)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, AttachmentScanJobStatus.Pending)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.LastError, (string?)null), cancellationToken) == 1,
            DeadLetterQueueNames.AttachmentProjection => await db.AttachmentScanProjections
                .Where(x => x.Id == id && x.Status == AttachmentScanProjectionStatus.DeadLetter)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, AttachmentScanProjectionStatus.Pending)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.LastError, (string?)null), cancellationToken) == 1,
            DeadLetterQueueNames.AttachmentConfirm => await db.AttachmentConfirmSagas
                .Where(x => x.Id == id && x.Status == AttachmentConfirmSagaStatus.Failed)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, AttachmentConfirmSagaStatus.Requested)
                    .SetProperty(x => x.AttemptCount, 0)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.CompletedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.LastError, (string?)null)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken) == 1,
            DeadLetterQueueNames.AttachmentBlobDelete => await db.AttachmentBlobDeleteJobs
                .Where(x => x.Id == id && x.Status == AttachmentBlobDeleteJobStatus.DeadLetter)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, AttachmentBlobDeleteJobStatus.Pending)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.LastError, (string?)null), cancellationToken) == 1,
            DeadLetterQueueNames.DataExport => await db.DataExportJobs
                .Where(x => x.Id == jobId && x.Status == DataExportJobStatus.Failed)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, DataExportJobStatus.Pending)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(x => x.Error, (string?)null), cancellationToken) == 1,
            DeadLetterQueueNames.DataExportBlobDelete => await ResetDataExportBlobDeleteAsync(
                jobId, now, cancellationToken),
            DeadLetterQueueNames.LoginAudit => await db.LoginAuditOutbox
                .Where(x => x.Id == id && x.Status == LoginAuditOutboxStatus.DeadLetter)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, LoginAuditOutboxStatus.Pending)
                    .SetProperty(x => x.AttemptCount, 0)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.LastError, (string?)null)
                    .SetProperty(x => x.CompletedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken) == 1,
            DeadLetterQueueNames.LoginRisk => await db.LoginRiskOutbox
                .Where(x => x.Id == id && x.Status == LoginRiskOutboxStatus.DeadLetter)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, LoginRiskOutboxStatus.Pending)
                    .SetProperty(x => x.AttemptCount, 0)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.LastError, (string?)null)
                    .SetProperty(x => x.CompletedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken) == 1,
            DeadLetterQueueNames.ModerationRevocation => await db.ModerationSessionRevocationOutbox
                .Where(x => x.Id == id && x.Status == ModerationSessionRevocationOutboxStatus.Dead)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, ModerationSessionRevocationOutboxStatus.Pending)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.LastError, (string?)null), cancellationToken) == 1,
            DeadLetterQueueNames.RealtimeOutbox => await db.RealtimeOutbox
                .Where(x => x.EventId == jobId && x.Status == (short)RealtimeOutboxStatus.Dead)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, (short)RealtimeOutboxStatus.Pending)
                    .SetProperty(x => x.PublishedAtMs, (long?)null)
                    .SetProperty(x => x.AttemptCount, 0)
                    .SetProperty(x => x.NextAttemptAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    .SetProperty(x => x.LockedBy, (string?)null)
                    .SetProperty(x => x.LockedUntilMs, (long?)null)
                    .SetProperty(x => x.LastError, (string?)null), cancellationToken) == 1,
            DeadLetterQueueNames.AccountDeletion => await db.Users
                .Where(x => x.Id == id && x.DeletionDeadLetterAt != null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.DeletionDeadLetterAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.DeletionNextAttemptAt, now)
                    .SetProperty(x => x.DeletionLastError, (string?)null)
                    .SetProperty(x => x.DeletionLeaseOwner, (string?)null)
                    .SetProperty(x => x.DeletionLeaseUntil, (DateTimeOffset?)null), cancellationToken) == 1,
            DeadLetterQueueNames.AccountCleanupSaga => await ResetAccountCleanupSagaAsync(jobId, cancellationToken),
            _ => false,
        };
    }

    private async Task<bool> ResetAccountCleanupSagaAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(jobId, out var deadLetterId))
            return false;
        var dead = await db.AccountCleanupDeadLetters.AsNoTracking()
            .Where(x => x.Id == deadLetterId)
            .Select(x => new { x.UserId })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (dead is null)
            return false;
        var replay = await accountCleanupSaga.TryReplayAsync(dead.UserId, cancellationToken)
            .ConfigureAwait(false);
        return replay.Outcome == AccountCleanupReplayOutcome.Replayed;
    }

    private async Task<bool> ResetDataExportBlobDeleteAsync(
        string jobId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var deadLetter = await db.DataExportJobs.AsNoTracking()
            .Where(x => x.Id == jobId && x.Status == DataExportJobStatus.DeleteDeadLetter)
            .Select(x => new { x.ConsumedAt })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (deadLetter is null)
            return false;

        var retryStatus = deadLetter.ConsumedAt is not null
            ? DataExportJobStatus.ConsumedPendingDelete
            : DataExportJobStatus.PendingDelete;
        return await db.DataExportJobs
            .Where(x => x.Id == jobId && x.Status == DataExportJobStatus.DeleteDeadLetter)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, retryStatus)
                .SetProperty(x => x.AttemptCount, 0)
                .SetProperty(x => x.NextAttemptAt, now)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(x => x.LeaseToken, (string?)null)
                .SetProperty(x => x.Error, (string?)null), cancellationToken) == 1;
    }

    private async Task AppendQueueAsync(
        List<DeadLetterItemDto> rows,
        string queue,
        int take,
        CancellationToken cancellationToken,
        string? exactJobId = null)
    {
        switch (queue)
        {
            case DeadLetterQueueNames.EmailOutbox:
                rows.AddRange(await db.EmailOutbox.AsNoTracking()
                    .Where(x => x.Status == EmailOutboxStatus.Dead
                                && (exactJobId == null || x.Id.ToString() == exactJobId))
                    .OrderByDescending(x => x.UpdatedAt).Take(take)
                    .Select(x => new DeadLetterItemDto(queue, x.Id.ToString(), null, x.Status.ToString(), x.AttemptCount,
                        new DateTimeOffset(x.CreatedAt), new DateTimeOffset(x.UpdatedAt), new DateTimeOffset(x.NextAttemptAt),
                        x.LastError, $"to={x.To};subject={x.Subject}", null, null))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
                break;
            case DeadLetterQueueNames.NotificationOutbox:
                rows.AddRange(await db.NotificationOutbox.AsNoTracking()
                    .Where(x => x.Status == NotificationOutboxStatus.Dead
                                && (exactJobId == null || x.Id.ToString() == exactJobId))
                    .OrderByDescending(x => x.UpdatedAt).Take(take)
                    .Select(x => new DeadLetterItemDto(queue, x.Id.ToString(), x.UserId, x.Status.ToString(), x.AttemptCount,
                        x.CreatedAt, x.UpdatedAt, x.NextAttemptAt, x.LastError, x.Type, null, null))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
                break;
            case DeadLetterQueueNames.AttachmentScan:
                rows.AddRange(await db.AttachmentScanJobs.AsNoTracking()
                    .Where(x => x.Status == AttachmentScanJobStatus.DeadLetter
                                && (exactJobId == null || x.Id.ToString() == exactJobId))
                    .OrderByDescending(x => x.CreatedAt).Take(take)
                    .Select(x => new DeadLetterItemDto(queue, x.Id.ToString(), x.UserId, x.Status, x.AttemptCount,
                        x.CreatedAt, x.CompletedAt, x.NextAttemptAt, x.LastError, x.AttachmentId, null, null))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
                break;
            case DeadLetterQueueNames.AttachmentProjection:
                rows.AddRange(await db.AttachmentScanProjections.AsNoTracking()
                    .Where(x => x.Status == AttachmentScanProjectionStatus.DeadLetter
                                && (exactJobId == null || x.Id.ToString() == exactJobId))
                    .OrderByDescending(x => x.CreatedAt).Take(take)
                    .Select(x => new DeadLetterItemDto(queue, x.Id.ToString(), x.UserId, x.Status, x.AttemptCount,
                        x.CreatedAt, x.CompletedAt, x.NextAttemptAt, x.LastError, x.AttachmentId, null, null))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
                break;
            case DeadLetterQueueNames.AttachmentConfirm:
                rows.AddRange(await db.AttachmentConfirmSagas.AsNoTracking()
                    .Where(x => x.Status == AttachmentConfirmSagaStatus.Failed
                                && (exactJobId == null || x.Id.ToString() == exactJobId))
                    .OrderByDescending(x => x.CreatedAt).Take(take)
                    .Select(x => new DeadLetterItemDto(queue, x.Id.ToString(), x.UserId, x.Status, x.AttemptCount,
                        x.CreatedAt, x.UpdatedAt, x.NextAttemptAt, x.LastError, x.AttachmentId, null, null))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
                break;
            case DeadLetterQueueNames.AttachmentBlobDelete:
                rows.AddRange(await db.AttachmentBlobDeleteJobs.AsNoTracking()
                    .Where(x => x.Status == AttachmentBlobDeleteJobStatus.DeadLetter
                                && (exactJobId == null || x.Id.ToString() == exactJobId))
                    .OrderByDescending(x => x.CreatedAt).Take(take)
                    .Select(x => new DeadLetterItemDto(queue, x.Id.ToString(), x.UserId, x.Status, x.AttemptCount,
                        x.CreatedAt, x.CompletedAt, x.NextAttemptAt, x.LastError, x.ObjectKey, null, null))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
                break;
            case DeadLetterQueueNames.DataExport:
                rows.AddRange(await db.DataExportJobs.AsNoTracking()
                    .Where(x => x.Status == DataExportJobStatus.Failed
                                && (exactJobId == null || x.Id == exactJobId))
                    .OrderByDescending(x => x.CreatedAt).Take(take)
                    .Select(x => new DeadLetterItemDto(queue, x.Id, x.UserId, x.Status, x.AttemptCount,
                        x.CreatedAt, null, x.NextAttemptAt, x.Error, x.ObjectKey, null, null))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
                break;
            case DeadLetterQueueNames.DataExportBlobDelete:
                rows.AddRange(await db.DataExportJobs.AsNoTracking()
                    .Where(x => x.Status == DataExportJobStatus.DeleteDeadLetter
                                && (exactJobId == null || x.Id == exactJobId))
                    .OrderByDescending(x => x.CreatedAt).Take(take)
                    .Select(x => new DeadLetterItemDto(queue, x.Id, x.UserId, x.Status, x.AttemptCount,
                        x.CreatedAt, null, x.NextAttemptAt, x.Error, x.ObjectKey, null, null))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
                break;
            case DeadLetterQueueNames.ModerationRevocation:
                rows.AddRange(await db.ModerationSessionRevocationOutbox.AsNoTracking()
                    .Where(x => x.Status == ModerationSessionRevocationOutboxStatus.Dead
                                && (exactJobId == null || x.Id.ToString() == exactJobId))
                    .OrderByDescending(x => x.CreatedAt).Take(take)
                    .Select(x => new DeadLetterItemDto(queue, x.Id.ToString(), x.UserId, x.Status.ToString(), x.AttemptCount,
                        x.CreatedAt, x.CompletedAt, x.NextAttemptAt, x.LastError, $"sourceReport={x.SourceReportId}", null, null))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
                break;
            case DeadLetterQueueNames.LoginAudit:
                rows.AddRange(await db.LoginAuditOutbox.AsNoTracking()
                    .Where(x => x.Status == LoginAuditOutboxStatus.DeadLetter
                                && (exactJobId == null || x.Id.ToString() == exactJobId))
                    .OrderByDescending(x => x.CreatedAt).Take(take)
                    .Select(x => new DeadLetterItemDto(queue, x.Id.ToString(), x.UserId, x.Status.ToString(), x.AttemptCount,
                        x.CreatedAt, x.UpdatedAt, x.NextAttemptAt, x.LastError, x.EventType.ToString(), null, null))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
                break;
            case DeadLetterQueueNames.LoginRisk:
                rows.AddRange(await db.LoginRiskOutbox.AsNoTracking()
                    .Where(x => x.Status == LoginRiskOutboxStatus.DeadLetter
                                && (exactJobId == null || x.Id.ToString() == exactJobId))
                    .OrderByDescending(x => x.CreatedAt).Take(take)
                    .Select(x => new DeadLetterItemDto(queue, x.Id.ToString(), x.UserId, x.Status.ToString(), x.AttemptCount,
                        x.CreatedAt, x.UpdatedAt, x.NextAttemptAt, x.LastError,
                        $"session={x.SessionId};ipChanged={x.IpChanged}", null, null))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
                break;
            case DeadLetterQueueNames.AccountDeletion:
                rows.AddRange(await db.Users.AsNoTracking()
                    .Where(x => x.DeletionDeadLetterAt != null
                                && (exactJobId == null || x.Id.ToString() == exactJobId))
                    .OrderByDescending(x => x.DeletionDeadLetterAt).Take(take)
                    .Select(x => new DeadLetterItemDto(queue, x.Id.ToString(), x.Id, "DeadLetter", x.DeletionAttemptCount,
                        x.DeletionScheduledAt, x.DeletionDeadLetterAt, x.DeletionNextAttemptAt, x.DeletionLastError, null, null, null))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
                break;
            case DeadLetterQueueNames.AccountCleanupSaga:
                rows.AddRange(await db.AccountCleanupDeadLetters.AsNoTracking()
                    .Where(x => exactJobId == null || x.Id.ToString() == exactJobId)
                    .OrderByDescending(x => x.CreatedAt).Take(take)
                    .Select(x => new DeadLetterItemDto(queue, x.Id.ToString(), x.UserId, "DeadLetter", (int)(x.DeliveryCount ?? 0),
                        x.CreatedAt, null, null, x.Reason, x.ReasonCode, null, null))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
                break;
            case DeadLetterQueueNames.RealtimeOutbox:
                rows.AddRange(await db.RealtimeOutbox.AsNoTracking()
                    .Where(x => x.Status == (short)RealtimeOutboxStatus.Dead
                                && (exactJobId == null || x.EventId == exactJobId))
                    .OrderByDescending(x => x.CreatedAtMs).Take(take)
                    .Select(x => new DeadLetterItemDto(queue, x.EventId, x.TargetUserId, "Dead", x.AttemptCount,
                        DateTimeOffset.FromUnixTimeMilliseconds(x.CreatedAtMs), null,
                        DateTimeOffset.FromUnixTimeMilliseconds(x.NextAttemptAtMs), x.LastError, x.PayloadJson, null, null))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
                break;
        }
    }

    private async Task AttachResolutionsAsync(
        List<DeadLetterItemDto> rows,
        CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            var resolution = await db.JobDeadLetterResolutions.AsNoTracking()
                .Where(x => x.Queue == row.Queue && x.JobId == row.JobId)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new { x.Action, x.CreatedAt })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (resolution is not null)
            {
                var index = rows.IndexOf(row);
                rows[index] = row with
                {
                    ResolutionAction = resolution.Action,
                    ResolutionAt = resolution.CreatedAt,
                };
            }
        }
    }

    private static DeadLetterActionResult Missing(string queue, string jobId)
        => new(false, "not_found", $"未找到死信：{queue}/{jobId}");

    private static string? NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(1000, reason.Trim().Length)];
}
