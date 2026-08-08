using Core.Interfaces;
using Core.Models.Attachment;
using Core.Models.Auth;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Infrastructure.Services.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Durable attachment-confirm orchestration. A claim owns one saga lease; each
/// external stage is idempotent and is followed by a fenced local transition.
/// </summary>
public sealed class AttachmentConfirmSagaService(
    UserDbContext db,
    IAttachmentStorage storage,
    IAttachmentMetadataStore metadata,
    AttachmentScanEnqueuer scanEnqueuer,
    IAttachmentBlobDeleteService blobDeletes,
    IMfaSecretProtector ticketProtector,
    IOptions<AttachmentStorageOptions> options,
    ILogger<AttachmentConfirmSagaService> logger) : IAttachmentConfirmSagaService
{
    private static readonly string ProcessOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:attachment-confirm";
    private static readonly string[] ActiveStatuses =
    [
        AttachmentConfirmSagaStatus.Requested,
        AttachmentConfirmSagaStatus.StorageConfirmed,
        AttachmentConfirmSagaStatus.MetadataScanning,
        AttachmentConfirmSagaStatus.Compensating,
    ];

    private bool IsNpgsql => db.Database.ProviderName?.Contains(
        "Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    public async Task<(AuthOperationResult Result, ConfirmAttachmentResponse? Response)> RequestAsync(
        long userId,
        ConfirmAttachmentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(request.ObjectKey))
            return (AuthOperationResult.Fail("InvalidObjectKey", "无效的附件对象键"), null);

        var objectKey = request.ObjectKey.Trim();
        if (!IsOwnedObjectKey(userId, objectKey))
            return (AuthOperationResult.Fail("InvalidObjectKey", "无效的附件对象键"), null);

        // Recovery is only safe after a storage implementation has observed a
        // prior ticketed confirmation. Keep the initial request ticket
        // mandatory for S3, where a user-prefixed object key alone is not an
        // upload reservation.
        if (storage is IAttachmentConfirmRecovery
            && string.IsNullOrWhiteSpace(request.Ticket))
            return (AuthOperationResult.Fail("UploadTicketRequired", "确认附件须提供上传票"), null);

        var attachmentId = NormalizeAttachmentId(request.AttachmentId, objectKey);
        if (string.IsNullOrWhiteSpace(attachmentId))
            return (AuthOperationResult.Fail("InvalidAttachmentId", "缺少有效的附件标识"), null);

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.DeletionEpoch, u.DeletionScheduledAt })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
            return (AuthOperationResult.Fail("NotFound", "用户不存在"), null);
        if (user.DeletionScheduledAt is not null)
            return (AuthOperationResult.Fail("AccountDeletionScheduled", "账号已进入注销流程"), null);

        var existing = await db.AttachmentConfirmSagas
            .FirstOrDefaultAsync(x => x.AttachmentId == attachmentId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.UserId != userId
                || !string.Equals(existing.ObjectKey, objectKey, StringComparison.Ordinal))
            {
                return (AuthOperationResult.Fail("Forbidden", "附件确认请求不匹配"), null);
            }

            if (existing.Status == AttachmentConfirmSagaStatus.Failed
                && !string.IsNullOrWhiteSpace(request.Ticket))
            {
                existing.Status = AttachmentConfirmSagaStatus.Requested;
                existing.ProtectedTicket = ticketProtector.Protect(request.Ticket.Trim());
                existing.UploaderDeletionEpoch = user.DeletionEpoch;
                existing.AttemptCount = 0;
                existing.NextAttemptAt = DateTimeOffset.UtcNow;
                existing.CompletedAt = null;
                existing.LastError = null;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                existing.LeaseOwner = null;
                existing.LeaseToken = null;
                existing.LeaseExpiresAt = null;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return (AuthOperationResult.Success(), ToResponse(existing));
        }

        var now = DateTimeOffset.UtcNow;
        var saga = new AttachmentConfirmSaga
        {
            AttachmentId = attachmentId,
            UserId = userId,
            ObjectKey = objectKey,
            ProtectedTicket = string.IsNullOrWhiteSpace(request.Ticket)
                ? null
                : ticketProtector.Protect(request.Ticket.Trim()),
            UploaderDeletionEpoch = user.DeletionEpoch,
            Status = AttachmentConfirmSagaStatus.Requested,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.AttachmentConfirmSagas.Add(saga);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent duplicate request is a successful idempotent retry;
            // the unique index is the arbitration boundary.
            db.ChangeTracker.Clear();
            var winner = await db.AttachmentConfirmSagas.AsNoTracking()
                .SingleOrDefaultAsync(x => x.AttachmentId == attachmentId, cancellationToken)
                .ConfigureAwait(false);
            if (winner is null || winner.UserId != userId)
                return (AuthOperationResult.Fail("ConfirmFailed", "附件确认意图保存失败"), null);
            return (AuthOperationResult.Success(), ToResponse(winner));
        }

        return (AuthOperationResult.Success(), ToResponse(saga));
    }

    public async Task<ConfirmAttachmentResponse?> GetStatusAsync(
        long userId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(attachmentId))
            return null;

        var saga = await db.AttachmentConfirmSagas.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.UserId == userId && x.AttachmentId == attachmentId,
                cancellationToken)
            .ConfigureAwait(false);
        return saga is null ? null : ToResponse(saga);
    }

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var claimed = await ClaimDueAsync(
                Math.Clamp(options.Value.ConfirmBatchSize, 1, 100), cancellationToken)
            .ConfigureAwait(false);
        var processed = 0;
        foreach (var saga in claimed)
        {
            if (await ProcessClaimedAsync(saga, cancellationToken).ConfigureAwait(false))
                processed++;
        }

        return processed;
    }

    public async Task<IReadOnlyList<AttachmentConfirmSaga>> ClaimDueAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        batchSize = Math.Clamp(batchSize, 1, 100);
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.AddSeconds(Math.Clamp(options.Value.ConfirmLeaseSeconds, 30, 900));
        var owner = $"{ProcessOwner}:{Guid.NewGuid():N}";
        if (owner.Length > 128)
            owner = owner[..128];

        if (IsNpgsql)
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE "T_AttachmentConfirmSaga" AS s
                SET "Status" = CASE WHEN s."Status" = 'Compensating' THEN 'Compensating' ELSE s."Status" END,
                    "LeaseOwner" = @owner,
                    "LeaseToken" = md5(random()::text || clock_timestamp()::text || s."Id"::text),
                    "LeaseExpiresAt" = @lease_until
                WHERE s."Id" IN (
                    SELECT c."Id"
                    FROM "T_AttachmentConfirmSaga" AS c
                    WHERE c."Status" IN ('Requested', 'StorageConfirmed', 'MetadataScanning', 'Compensating')
                      AND c."NextAttemptAt" <= @now
                      AND (c."LeaseExpiresAt" IS NULL OR c."LeaseExpiresAt" < @now)
                    ORDER BY c."NextAttemptAt", c."Id"
                    FOR UPDATE SKIP LOCKED
                    LIMIT @batch
                )
                RETURNING s."Id";
                """;
            AddParameter(command, "owner", owner);
            AddParameter(command, "lease_until", leaseUntil);
            AddParameter(command, "now", now);
            AddParameter(command, "batch", batchSize);

            var ids = new List<long>(batchSize);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                             .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    ids.Add(reader.GetInt64(0));
            }

            return ids.Count == 0
                ? []
                : await db.AttachmentConfirmSagas.AsNoTracking()
                    .Where(x => ids.Contains(x.Id))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
        }

        var due = await db.AttachmentConfirmSagas
            .Where(x => ActiveStatuses.Contains(x.Status))
            .Where(x => x.NextAttemptAt <= now
                && (x.LeaseExpiresAt == null || x.LeaseExpiresAt < now))
            .OrderBy(x => x.NextAttemptAt)
            .ThenBy(x => x.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var saga in due)
        {
            saga.LeaseOwner = owner;
            saga.LeaseToken = Guid.NewGuid().ToString("N");
            saga.LeaseExpiresAt = leaseUntil;
        }

        if (due.Count > 0)
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return due;
    }

    public async Task<bool> ProcessClaimedAsync(
        AttachmentConfirmSaga claimed,
        CancellationToken cancellationToken = default)
    {
        if (!OwnsLease(claimed))
            return false;

        try
        {
            await ExecuteClaimedAsync(claimed, cancellationToken).ConfigureAwait(false);
            return await CompleteClaimedAsync(claimed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "附件确认 Saga 推进失败 SagaId={SagaId} AttachmentId={AttachmentId}",
                claimed.Id,
                claimed.AttachmentId);
            return await RetryClaimedAsync(claimed, ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the external stages while retaining the row lease. The shared
    /// executor renews that lease and cancels this operation if ownership is
    /// lost; the final local lease clear is performed separately.
    /// </summary>
    public async Task ExecuteClaimedAsync(
        AttachmentConfirmSaga claimed,
        CancellationToken cancellationToken = default)
    {
        if (!OwnsLease(claimed))
            throw new InvalidOperationException("附件确认 Saga 租约无效");

        // Claim returns a detached row. Re-read the owner/token immediately
        // before any external side effect so a reclaimed saga cannot start a
        // second storage confirmation merely because its in-memory snapshot
        // still contains non-empty lease fields.
        if (!await IsLeaseStillOwnedAsync(claimed, cancellationToken)
                .ConfigureAwait(false))
            throw new InvalidOperationException("附件确认 Saga 租约已丢失");

        var userState = await db.Users.AsNoTracking()
            .Where(x => x.Id == claimed.UserId)
            .Select(x => new { x.DeletionEpoch, x.DeletionScheduledAt })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userState is null
            || userState.DeletionScheduledAt is not null
            || userState.DeletionEpoch != claimed.UploaderDeletionEpoch)
        {
            claimed.Status = AttachmentConfirmSagaStatus.Compensating;
            claimed.LastError = "account_deletion_epoch_changed";
            if (!await SaveProgressFencedAsync(claimed, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("附件确认 Saga 租约已丢失");
            await CompensateAsync(claimed, cancellationToken, clearLease: false)
                .ConfigureAwait(false);
            return;
        }

        switch (claimed.Status)
        {
            case AttachmentConfirmSagaStatus.Requested:
            {
                if (!await IsLeaseStillOwnedAsync(claimed, cancellationToken)
                        .ConfigureAwait(false))
                    throw new InvalidOperationException("附件确认 Saga 租约已丢失");
                var ticket = UnprotectTicket(claimed.ProtectedTicket);
                var result = await storage.ConfirmObjectAsync(
                        claimed.UserId,
                        claimed.ObjectKey,
                        ticket,
                        claimed.AttachmentId,
                        cancellationToken)
                    .ConfigureAwait(false);

                // A successful storage confirmation followed by a lost DB
                // write consumes the ticket. Retry without it and let the
                // object store prove the same object is present.
                if (!result.Ok && !string.IsNullOrWhiteSpace(ticket)
                    && IsConsumedTicketError(result.Error))
                {
                    result = storage is IAttachmentConfirmRecovery recovery
                        ? await recovery.RecoverConfirmedObjectAsync(
                                claimed.UserId,
                                claimed.ObjectKey,
                                claimed.AttachmentId,
                                cancellationToken)
                            .ConfigureAwait(false)
                        : await storage.ConfirmObjectAsync(
                                claimed.UserId,
                                claimed.ObjectKey,
                                ticket: null,
                                claimed.AttachmentId,
                                cancellationToken)
                            .ConfigureAwait(false);
                }

                if (!result.Ok || string.IsNullOrWhiteSpace(result.ObjectKey))
                {
                    var error = result.Error ?? "storage_confirm_failed";
                    if (IsPermanentConfirmError(result.Error))
                    {
                        claimed.Status = AttachmentConfirmSagaStatus.Compensating;
                        claimed.LastError = error;
                        if (!await SaveProgressFencedAsync(claimed, cancellationToken)
                                .ConfigureAwait(false))
                            throw new InvalidOperationException("附件确认 Saga 租约已丢失");
                        await CompensateAsync(claimed, cancellationToken, clearLease: false)
                            .ConfigureAwait(false);
                        return;
                    }

                    throw new InvalidOperationException(error);
                }

                claimed.ConfirmedObjectKey = result.ObjectKey;
                claimed.ContentType = result.ContentType;
                claimed.SizeBytes = result.SizeBytes;
                claimed.OriginalName = result.OriginalName;
                claimed.ProtectedTicket = null;
                claimed.Status = AttachmentConfirmSagaStatus.StorageConfirmed;
                if (!await SaveProgressFencedAsync(claimed, cancellationToken)
                        .ConfigureAwait(false))
                    throw new InvalidOperationException("附件确认 Saga 租约已丢失");
                goto case AttachmentConfirmSagaStatus.StorageConfirmed;
            }

            case AttachmentConfirmSagaStatus.StorageConfirmed:
                if (!await IsLeaseStillOwnedAsync(claimed, cancellationToken)
                        .ConfigureAwait(false))
                    throw new InvalidOperationException("附件确认 Saga 租约已丢失");
                if (!metadata.IsAvailable)
                    throw new InvalidOperationException(metadata.UnavailableReason);
                await metadata.MarkUploadedScanningAsync(
                        claimed.AttachmentId,
                        claimed.UserId,
                        claimed.SizeBytes,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                claimed.Status = AttachmentConfirmSagaStatus.MetadataScanning;
                if (!await SaveProgressFencedAsync(claimed, cancellationToken)
                        .ConfigureAwait(false))
                    throw new InvalidOperationException("附件确认 Saga 租约已丢失");
                goto case AttachmentConfirmSagaStatus.MetadataScanning;

            case AttachmentConfirmSagaStatus.MetadataScanning:
                if (!await IsLeaseStillOwnedAsync(claimed, cancellationToken)
                        .ConfigureAwait(false))
                    throw new InvalidOperationException("附件确认 Saga 租约已丢失");
                await scanEnqueuer.EnqueueAsync(
                        claimed.AttachmentId,
                        claimed.UserId,
                        claimed.ConfirmedObjectKey ?? claimed.ObjectKey,
                        claimed.ContentType,
                        claimed.OriginalName,
                        claimed.SizeBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                claimed.Status = AttachmentConfirmSagaStatus.ScanQueued;
                claimed.NextAttemptAt = DateTimeOffset.UtcNow;
                if (!await SaveProgressFencedAsync(claimed, cancellationToken)
                        .ConfigureAwait(false))
                    throw new InvalidOperationException("附件确认 Saga 租约已丢失");
                return;

            case AttachmentConfirmSagaStatus.Compensating:
                await CompensateAsync(claimed, cancellationToken, clearLease: false)
                    .ConfigureAwait(false);
                return;

            default:
                claimed.Status = AttachmentConfirmSagaStatus.Failed;
                if (!await SaveProgressFencedAsync(claimed, cancellationToken)
                        .ConfigureAwait(false))
                    throw new InvalidOperationException("附件确认 Saga 租约已丢失");
                return;
        }
    }

    public Task<bool> CompleteClaimedAsync(
        AttachmentConfirmSaga claimed,
        CancellationToken cancellationToken = default)
    {
        if (claimed.Status is not AttachmentConfirmSagaStatus.ScanQueued
            and not AttachmentConfirmSagaStatus.Failed
            and not AttachmentConfirmSagaStatus.Completed)
            return Task.FromResult(false);

        return ClearLeaseFencedAsync(claimed, cancellationToken);
    }

    public Task<bool> RetryClaimedAsync(
        AttachmentConfirmSaga claimed,
        string error,
        CancellationToken cancellationToken = default)
        => RetryAsync(claimed, error, cancellationToken);

    public Task<bool> DeadLetterClaimedAsync(
        AttachmentConfirmSaga claimed,
        string error,
        CancellationToken cancellationToken = default)
        => RetryAsync(claimed, error, cancellationToken);

    private Task<bool> ClearLeaseFencedAsync(
        AttachmentConfirmSaga claimed,
        CancellationToken cancellationToken)
        => SaveProgressFencedAsync(claimed, cancellationToken, clearLease: true);

    public async Task<LeaseRenewalResult> RenewLeaseAsync(
        long sagaId,
        string leaseOwner,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(
            Math.Clamp(options.Value.ConfirmLeaseSeconds, 30, 900));
        try
        {
            var updated = await db.AttachmentConfirmSagas
                .Where(x => x.Id == sagaId
                    && x.LeaseOwner == leaseOwner
                    && x.LeaseToken == leaseToken
                    && x.Status != AttachmentConfirmSagaStatus.Failed
                    && x.Status != AttachmentConfirmSagaStatus.Completed)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.LeaseExpiresAt, until), cancellationToken)
                .ConfigureAwait(false);
            return updated == 1 ? LeaseRenewalResult.Renewed : LeaseRenewalResult.LeaseLost;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "附件确认 Saga 租约续租失败 SagaId={SagaId}", sagaId);
            return LeaseRenewalResult.TransientFailure;
        }
    }

    public async Task CompleteScanAsync(
        string attachmentId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (!IsNpgsql)
        {
            var tracked = await db.AttachmentConfirmSagas
                .FirstOrDefaultAsync(
                    x => x.AttachmentId == attachmentId
                         && x.UserId == userId
                         && x.Status == AttachmentConfirmSagaStatus.ScanQueued,
                    cancellationToken)
                .ConfigureAwait(false);
            if (tracked is null)
                return;

            tracked.Status = AttachmentConfirmSagaStatus.Completed;
            tracked.CompletedAt = now;
            tracked.UpdatedAt = now;
            tracked.LastError = null;
            tracked.LeaseOwner = null;
            tracked.LeaseToken = null;
            tracked.LeaseExpiresAt = null;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await db.AttachmentConfirmSagas
            .Where(x => x.AttachmentId == attachmentId
                && x.UserId == userId
                && x.Status == AttachmentConfirmSagaStatus.ScanQueued)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status, AttachmentConfirmSagaStatus.Completed)
                    .SetProperty(x => x.CompletedAt, now)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.LastError, (string?)null)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> CompensateAsync(
        AttachmentConfirmSaga claimed,
        CancellationToken cancellationToken,
        bool clearLease = true)
    {
        try
        {
            var ticket = UnprotectTicket(claimed.ProtectedTicket);
            if (!string.IsNullOrWhiteSpace(ticket))
                await storage.CancelUploadTicketAsync(ticket, cancellationToken).ConfigureAwait(false);

            if (metadata.IsAvailable)
            {
                await metadata.MarkAbandonedAsync(
                        [claimed.AttachmentId], cancellationToken)
                    .ConfigureAwait(false);
            }

            await blobDeletes.EnqueueAsync(
                    [(claimed.ConfirmedObjectKey ?? claimed.ObjectKey, claimed.AttachmentId)],
                    claimed.UserId,
                    cancellationToken)
                .ConfigureAwait(false);

            claimed.Status = AttachmentConfirmSagaStatus.Failed;
            claimed.CompletedAt = DateTimeOffset.UtcNow;
            claimed.LastError ??= "compensated";
            return await SaveProgressFencedAsync(
                    claimed,
                    cancellationToken,
                    clearLease)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!clearLease)
                throw;

            return await RetryAsync(claimed, $"compensation_failed:{ex.Message}", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<bool> RetryAsync(
        AttachmentConfirmSaga claimed,
        string error,
        CancellationToken cancellationToken)
    {
        var attempt = Math.Max(1, claimed.AttemptCount + 1);
        var max = Math.Max(1, options.Value.MaxConfirmAttempts);
        var now = DateTimeOffset.UtcNow;
        claimed.AttemptCount = attempt;
        claimed.LastError = Truncate(error, 500);
        claimed.NextAttemptAt = now.Add(ComputeBackoff(attempt));
        if (attempt >= max && claimed.Status != AttachmentConfirmSagaStatus.Compensating)
            claimed.Status = AttachmentConfirmSagaStatus.Compensating;

        // Keep ownership while entering compensation. Clearing the lease first
        // would let another worker claim the row before the blob/metadata
        // compensation and would make the subsequent fenced write impossible.
        var compensating = claimed.Status == AttachmentConfirmSagaStatus.Compensating;
        var saved = await SaveProgressFencedAsync(
                claimed,
                cancellationToken,
                clearLease: !compensating)
            .ConfigureAwait(false);
        if (saved && compensating)
            return await CompensateAsync(claimed, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    private async Task<bool> SaveProgressFencedAsync(
        AttachmentConfirmSaga target,
        CancellationToken cancellationToken,
        bool clearLease = false)
    {
        var expectedOwner = target.LeaseOwner;
        var expectedToken = target.LeaseToken;
        if (string.IsNullOrWhiteSpace(expectedOwner)
            || string.IsNullOrWhiteSpace(expectedToken))
            return false;

        if (!IsNpgsql)
        {
            var current = await db.AttachmentConfirmSagas
                .FirstOrDefaultAsync(x => x.Id == target.Id, cancellationToken)
                .ConfigureAwait(false);
            if (current is null
                || !string.Equals(current.LeaseOwner, expectedOwner, StringComparison.Ordinal)
                || !string.Equals(current.LeaseToken, expectedToken, StringComparison.Ordinal))
                return false;

            current.Status = target.Status;
            current.AttemptCount = target.AttemptCount;
            current.NextAttemptAt = target.NextAttemptAt;
            current.UpdatedAt = DateTimeOffset.UtcNow;
            current.CompletedAt = target.CompletedAt;
            current.LastError = target.LastError;
            current.ProtectedTicket = target.ProtectedTicket;
            current.ConfirmedObjectKey = target.ConfirmedObjectKey;
            current.ContentType = target.ContentType;
            current.SizeBytes = target.SizeBytes;
            current.OriginalName = target.OriginalName;
            current.LeaseOwner = clearLease ? null : target.LeaseOwner;
            current.LeaseToken = clearLease ? null : target.LeaseToken;
            if (clearLease)
                current.LeaseExpiresAt = null;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var updated = await db.AttachmentConfirmSagas
            .Where(x => x.Id == target.Id
                && x.LeaseOwner == expectedOwner
                && x.LeaseToken == expectedToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status, target.Status)
                    .SetProperty(x => x.AttemptCount, target.AttemptCount)
                    .SetProperty(x => x.NextAttemptAt, target.NextAttemptAt)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow)
                    .SetProperty(x => x.CompletedAt, target.CompletedAt)
                    .SetProperty(x => x.LastError, target.LastError)
                    .SetProperty(x => x.ProtectedTicket, target.ProtectedTicket)
                    .SetProperty(x => x.ConfirmedObjectKey, target.ConfirmedObjectKey)
                    .SetProperty(x => x.ContentType, target.ContentType)
                    .SetProperty(x => x.SizeBytes, target.SizeBytes)
                    .SetProperty(x => x.OriginalName, target.OriginalName)
                    .SetProperty(x => x.LeaseOwner, clearLease ? null : target.LeaseOwner)
                    .SetProperty(x => x.LeaseToken, clearLease ? null : target.LeaseToken)
                    // Never write the claim-time expiry back after a heartbeat
                    // renewed it. A non-clearing progress update keeps the
                    // database value evaluated at statement time.
                    .SetProperty(
                        x => x.LeaseExpiresAt,
                        x => clearLease
                            ? (DateTimeOffset?)null
                            : x.LeaseExpiresAt),
                cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    private Task<bool> SetStatusFencedAsync(
        AttachmentConfirmSaga target,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        target.Status = status;
        target.LastError = error;
        target.NextAttemptAt = DateTimeOffset.UtcNow;
        return SaveProgressFencedAsync(target, cancellationToken);
    }

    private bool OwnsLease(AttachmentConfirmSaga saga) =>
        !string.IsNullOrWhiteSpace(saga.LeaseOwner)
        && !string.IsNullOrWhiteSpace(saga.LeaseToken);

    private Task<bool> IsLeaseStillOwnedAsync(
        AttachmentConfirmSaga claimed,
        CancellationToken cancellationToken)
        => db.AttachmentConfirmSagas.AsNoTracking().AnyAsync(
            x => x.Id == claimed.Id
                 && x.LeaseOwner == claimed.LeaseOwner
                 && x.LeaseToken == claimed.LeaseToken
                 && x.Status != AttachmentConfirmSagaStatus.Failed
                 && x.Status != AttachmentConfirmSagaStatus.Completed,
            cancellationToken);

    private string? UnprotectTicket(string? protectedTicket)
    {
        if (string.IsNullOrWhiteSpace(protectedTicket))
            return null;
        return ticketProtector.Unprotect(protectedTicket);
    }

    private static bool IsOwnedObjectKey(long userId, string objectKey) =>
        objectKey.StartsWith($"{userId}/", StringComparison.Ordinal)
        || objectKey.StartsWith($"attachments/{userId}/", StringComparison.Ordinal);

    private static string? NormalizeAttachmentId(string? requested, string objectKey)
    {
        var candidate = string.IsNullOrWhiteSpace(requested)
            ? objectKey[(objectKey.LastIndexOf('/') + 1)..]
            : requested.Trim();
        if (candidate.Length is 0 or > 64
            || candidate.Contains('/', StringComparison.Ordinal)
            || candidate.Contains('\\', StringComparison.Ordinal)
            || candidate.Contains("..", StringComparison.Ordinal))
            return null;
        return candidate;
    }

    private static bool IsConsumedTicketError(string? error) =>
        error?.Contains("票无效", StringComparison.OrdinalIgnoreCase) == true
        || error?.Contains("expired", StringComparison.OrdinalIgnoreCase) == true
        || error?.Contains("invalid", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsPermanentConfirmError(string? error) =>
        error?.Contains("对象键", StringComparison.OrdinalIgnoreCase) == true
        || error?.Contains("用户不匹配", StringComparison.OrdinalIgnoreCase) == true
        || error?.Contains("缺少", StringComparison.OrdinalIgnoreCase) == true
        || error?.Contains("大小超限", StringComparison.OrdinalIgnoreCase) == true;

    private static TimeSpan ComputeBackoff(int attempt)
    {
        var seconds = Math.Min(3600, 5 * Math.Pow(2, Math.Min(10, Math.Max(0, attempt - 1))));
        var jitter = Random.Shared.NextDouble() * Math.Max(1, seconds * 0.2);
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static ConfirmAttachmentResponse ToResponse(AttachmentConfirmSaga saga)
    {
#pragma warning disable CS0618
        return new ConfirmAttachmentResponse
        {
            SagaId = saga.Id,
            AttachmentId = saga.AttachmentId,
            ObjectKey = saga.ConfirmedObjectKey ?? saga.ObjectKey,
            DownloadPath = AttachmentApiPaths.DownloadPath(saga.AttachmentId),
            Status = saga.Status == AttachmentConfirmSagaStatus.Completed ? "Confirmed" : "Scanning",
            SagaStatus = saga.Status,
            PublicUrl = string.Empty,
        };
#pragma warning restore CS0618
    }

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

public sealed class AttachmentConfirmSagaWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AttachmentStorageOptions> options,
    IOptions<WorkerConcurrencyOptions> workerConcurrencyOptions,
    ILeasedJobStore<AttachmentConfirmSaga> sagaStore,
    LeasedJobExecutor<AttachmentConfirmSaga> executor,
    ILogger<AttachmentConfirmSagaWorker> logger) : Microsoft.Extensions.Hosting.BackgroundService
{
    private const string WorkerName = "attachment_confirm";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        var poll = TimeSpan.FromSeconds(Math.Clamp(options.Value.ScanBackoffSeconds, 2, 60));
        var workerConcurrency = Math.Max(1, workerConcurrencyOptions.Value.AttachmentConfirm);
        var leaseDuration = TimeSpan.FromSeconds(
            Math.Clamp(options.Value.ConfirmLeaseSeconds, 30, 900));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var completed = await executor.DrainAsync(
                        WorkerName,
                        workerConcurrency,
                        leaseDuration,
                        sagaStore,
                        ExecuteClaimedAsync,
                        saga => saga.AttemptCount + 1 >= Math.Max(1, options.Value.MaxConfirmAttempts),
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
                logger.LogWarning(ex, "附件确认 Saga Worker 轮询异常");
                await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteClaimedAsync(
        AttachmentConfirmSaga claimed,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var saga = scope.ServiceProvider.GetRequiredService<IAttachmentConfirmSagaService>();
        await saga.ExecuteClaimedAsync(claimed, cancellationToken).ConfigureAwait(false);
    }
}
