using Core.Interfaces;
using Core.Models.Auth;
using Core.Models.Export;
using Core.Models.Identity;
using Core.Models.User;
using Core.Settings;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Infrastructure.Services.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Durable avatar finalization. The object store is never treated as the
/// transaction boundary: every transition is persisted and fenced before the
/// next external side effect is attempted.
/// </summary>
public sealed class AvatarFinalizationSagaService(
    UserDbContext db,
    IAvatarStorage storage,
    IAttachmentBlobDeleteService blobDeletes,
    IMfaSecretProtector ticketProtector,
    IOptions<AvatarStorageOptions> options,
    ILogger<AvatarFinalizationSagaService> logger) : IAvatarFinalizationSagaService
{
    private static readonly string ProcessOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:avatar-finalization";
    private static readonly string[] ActiveStatuses =
    [
        AvatarFinalizationSagaStatus.Requested,
        AvatarFinalizationSagaStatus.StorageConfirmed,
        AvatarFinalizationSagaStatus.MetadataCommitted,
        AvatarFinalizationSagaStatus.Compensating,
    ];

    private bool IsNpgsql => db.Database.ProviderName?.Contains(
        "Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    public async Task<(AuthOperationResult Result, AvatarFinalizationStatusDto? Response)> RequestAsync(
        long userId,
        string objectKey,
        string? ticket = null,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(objectKey))
            return (AuthOperationResult.Fail("InvalidObjectKey", "无效的头像对象键"), null);

        objectKey = objectKey.Trim();
        if (!IsOwnedObjectKey(userId, objectKey))
            return (AuthOperationResult.Fail("InvalidObjectKey", "无效的头像对象键"), null);

        var user = await db.Users.AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new
            {
                x.AvatarUrl,
                x.AvatarVersion,
                x.DeletionEpoch,
                x.DeletionScheduledAt,
                x.AccountState,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
            return (AuthOperationResult.Fail("NotFound", "用户不存在"), null);
        if (user.DeletionScheduledAt is not null
            || user.AccountState is AccountState.DeletionPending or AccountState.Deleted)
        {
            return (AuthOperationResult.Fail(
                "AccountDeletionScheduled",
                "账号已进入注销流程"), null);
        }

        var existing = await db.Set<AvatarFinalizationSaga>()
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.ObjectKey == objectKey)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Status == AvatarFinalizationSagaStatus.Failed
                && !string.IsNullOrWhiteSpace(ticket))
            {
                var retryNow = DateTimeOffset.UtcNow;
                int retry;
                if (!db.Database.IsRelational())
                {
                    var tracked = await db.Set<AvatarFinalizationSaga>()
                        .SingleOrDefaultAsync(x => x.Id == existing.Id, cancellationToken)
                        .ConfigureAwait(false);
                    if (tracked is null
                        || tracked.UserId != userId
                        || tracked.Status != AvatarFinalizationSagaStatus.Failed)
                    {
                        retry = 0;
                    }
                    else
                    {
                        tracked.Status = AvatarFinalizationSagaStatus.Requested;
                        tracked.ProtectedTicket = ticketProtector.Protect(ticket.Trim());
                        tracked.ExpectedAvatarVersion = user.AvatarVersion;
                        tracked.OldAvatarUrl = user.AvatarUrl;
                        tracked.UploaderDeletionEpoch = user.DeletionEpoch;
                        tracked.AttemptCount = 0;
                        tracked.NextAttemptAt = retryNow;
                        tracked.CompletedAt = null;
                        tracked.LastError = null;
                        tracked.LeaseOwner = null;
                        tracked.LeaseToken = null;
                        tracked.LeaseExpiresAt = null;
                        tracked.UpdatedAt = retryNow;
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        retry = 1;
                    }
                }
                else
                {
                    retry = await db.Set<AvatarFinalizationSaga>()
                        .Where(x => x.Id == existing.Id
                                    && x.UserId == userId
                                    && x.Status == AvatarFinalizationSagaStatus.Failed)
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(x => x.Status, AvatarFinalizationSagaStatus.Requested)
                                .SetProperty(x => x.ProtectedTicket, ticketProtector.Protect(ticket.Trim()))
                                .SetProperty(x => x.ExpectedAvatarVersion, user.AvatarVersion)
                                .SetProperty(x => x.OldAvatarUrl, user.AvatarUrl)
                                .SetProperty(x => x.UploaderDeletionEpoch, user.DeletionEpoch)
                                .SetProperty(x => x.AttemptCount, 0)
                                .SetProperty(x => x.NextAttemptAt, retryNow)
                                .SetProperty(x => x.CompletedAt, (DateTimeOffset?)null)
                                .SetProperty(x => x.LastError, (string?)null)
                                .SetProperty(x => x.LeaseOwner, (string?)null)
                                .SetProperty(x => x.LeaseToken, (string?)null)
                                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                                .SetProperty(x => x.UpdatedAt, retryNow),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                if (retry == 1)
                {
                    existing = await db.Set<AvatarFinalizationSaga>()
                        .AsNoTracking()
                        .SingleAsync(x => x.Id == existing.Id, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return (AuthOperationResult.Success(), ToResponse(existing));
        }

        var now = DateTimeOffset.UtcNow;
        var saga = new AvatarFinalizationSaga
        {
            UserId = userId,
            ObjectKey = objectKey,
            ProtectedTicket = string.IsNullOrWhiteSpace(ticket)
                ? null
                : ticketProtector.Protect(ticket.Trim()),
            OldAvatarUrl = user.AvatarUrl,
            ExpectedAvatarVersion = user.AvatarVersion,
            UploaderDeletionEpoch = user.DeletionEpoch,
            Status = AvatarFinalizationSagaStatus.Requested,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Set<AvatarFinalizationSaga>().Add(saga);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winner = await db.Set<AvatarFinalizationSaga>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.UserId == userId && x.ObjectKey == objectKey,
                    cancellationToken)
                .ConfigureAwait(false);
            return winner is null
                ? (AuthOperationResult.Fail("ConfirmFailed", "头像确认意图保存失败"), null)
                : (AuthOperationResult.Success(), ToResponse(winner));
        }

        return (AuthOperationResult.Success(), ToResponse(saga));
    }

    public async Task<AvatarFinalizationStatusDto?> GetStatusAsync(
        long userId,
        long sagaId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || sagaId <= 0)
            return null;

        var saga = await db.Set<AvatarFinalizationSaga>().AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == sagaId && x.UserId == userId,
                cancellationToken)
            .ConfigureAwait(false);
        return saga is null ? null : ToResponse(saga);
    }

    public async Task<IReadOnlyList<AvatarFinalizationSaga>> ClaimDueAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        batchSize = Math.Clamp(batchSize, 1, 100);
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.AddSeconds(Math.Clamp(options.Value.FinalizationLeaseSeconds, 30, 900));
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
                UPDATE "T_AvatarFinalizationSaga" AS s
                SET "LeaseOwner" = @owner,
                    "LeaseToken" = md5(random()::text || clock_timestamp()::text || s."Id"::text),
                    "LeaseExpiresAt" = @lease_until,
                    "AttemptCount" = s."AttemptCount" + 1,
                    "UpdatedAt" = @now
                WHERE s."Id" IN (
                    SELECT c."Id"
                    FROM "T_AvatarFinalizationSaga" AS c
                    WHERE c."Status" IN ('Requested', 'StorageConfirmed', 'MetadataCommitted', 'Compensating')
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
                : await db.Set<AvatarFinalizationSaga>().AsNoTracking()
                    .Where(x => ids.Contains(x.Id))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
        }

        var due = await db.Set<AvatarFinalizationSaga>()
            .Where(x => ActiveStatuses.Contains(x.Status)
                        && x.NextAttemptAt <= now
                        && (x.LeaseExpiresAt == null || x.LeaseExpiresAt < now))
            .OrderBy(x => x.NextAttemptAt)
            .ThenBy(x => x.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var item in due)
        {
            item.LeaseOwner = owner;
            item.LeaseToken = Guid.NewGuid().ToString("N");
            item.LeaseExpiresAt = leaseUntil;
            item.AttemptCount++;
            item.UpdatedAt = now;
        }

        if (due.Count > 0)
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return due;
    }

    public async Task ExecuteClaimedAsync(
        AvatarFinalizationSaga claimed,
        CancellationToken cancellationToken = default)
    {
        if (!OwnsLease(claimed)
            || !await IsLeaseStillOwnedAsync(claimed, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("头像 Finalization Saga 租约无效");

        var user = await db.Users.AsNoTracking()
            .Where(x => x.Id == claimed.UserId)
            .Select(x => new
            {
                x.AvatarUrl,
                x.AvatarVersion,
                x.DeletionEpoch,
                x.DeletionScheduledAt,
                x.AccountState,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (user is null
            || user.DeletionScheduledAt is not null
            || user.AccountState is AccountState.DeletionPending or AccountState.Deleted
            || user.DeletionEpoch != claimed.UploaderDeletionEpoch)
        {
            await CompensateAsync(claimed, "account_deletion_epoch_changed", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        switch (claimed.Status)
        {
            case AvatarFinalizationSagaStatus.Requested:
            {
                var ticket = UnprotectTicket(claimed.ProtectedTicket);
                var result = await storage.ConfirmObjectAsync(
                        claimed.UserId,
                        claimed.ObjectKey,
                        ticket,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Ok && storage is IAvatarConfirmRecovery recovery)
                {
                    // This is the crash-recovery leg after a provider consumed
                    // the ticket but the first local progress write was lost.
                    result = await recovery.RecoverConfirmedObjectAsync(
                            claimed.UserId,
                            claimed.ObjectKey,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!result.Ok || string.IsNullOrWhiteSpace(result.ObjectKey))
                    throw new InvalidOperationException(result.Error ?? "头像对象确认失败");

                claimed.FinalObjectKey = result.ObjectKey;
                claimed.PublicUrl = result.PublicUrl;
                claimed.ProtectedTicket = null;
                claimed.Status = AvatarFinalizationSagaStatus.StorageConfirmed;
                claimed.UpdatedAt = DateTimeOffset.UtcNow;
                if (!await SaveProgressFencedAsync(claimed, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("头像 Finalization Saga 租约已丢失");
                goto case AvatarFinalizationSagaStatus.StorageConfirmed;
            }

            case AvatarFinalizationSagaStatus.StorageConfirmed:
            {
                var finalKey = claimed.FinalObjectKey;
                if (string.IsNullOrWhiteSpace(finalKey) || string.IsNullOrWhiteSpace(claimed.PublicUrl))
                    throw new InvalidOperationException("头像最终对象信息缺失");

                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                    : null;

                await blobDeletes.EnqueueAvatarCandidatesAsync(
                        [finalKey], claimed.UserId, cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(claimed.OldAvatarUrl)
                    && !string.Equals(claimed.OldAvatarUrl, claimed.PublicUrl, StringComparison.Ordinal))
                {
                    await blobDeletes.EnqueueAvatarAsync(
                            [claimed.OldAvatarUrl], claimed.UserId, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (claimed.ExpectedAvatarVersion == long.MaxValue)
                    throw new InvalidOperationException("头像版本已达到最大值");

                var updated = await CommitAvatarMetadataAsync(claimed, cancellationToken)
                    .ConfigureAwait(false);

                if (updated == 0)
                {
                    var currentUrl = await db.Users.AsNoTracking()
                        .Where(x => x.Id == claimed.UserId)
                        .Select(x => x.AvatarUrl)
                        .SingleOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.Equals(currentUrl, claimed.PublicUrl, StringComparison.Ordinal))
                    {
                        claimed.Status = AvatarFinalizationSagaStatus.Abandoned;
                        claimed.LastError = "avatar_version_conflict";
                        claimed.CompletedAt = DateTimeOffset.UtcNow;
                        claimed.UpdatedAt = claimed.CompletedAt.Value;
                        if (!await SaveProgressFencedAsync(claimed, cancellationToken)
                                .ConfigureAwait(false))
                            throw new InvalidOperationException("头像 Finalization Saga 租约已丢失");
                    }
                    else
                    {
                        claimed.Status = AvatarFinalizationSagaStatus.MetadataCommitted;
                        claimed.UpdatedAt = DateTimeOffset.UtcNow;
                        if (!await SaveProgressFencedAsync(claimed, cancellationToken)
                                .ConfigureAwait(false))
                            throw new InvalidOperationException("头像 Finalization Saga 租约已丢失");
                    }
                }
                else
                {
                    claimed.Status = AvatarFinalizationSagaStatus.MetadataCommitted;
                    claimed.UpdatedAt = DateTimeOffset.UtcNow;
                    if (!await SaveProgressFencedAsync(claimed, cancellationToken)
                            .ConfigureAwait(false))
                        throw new InvalidOperationException("头像 Finalization Saga 租约已丢失");
                }

                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                goto case AvatarFinalizationSagaStatus.MetadataCommitted;
            }

            case AvatarFinalizationSagaStatus.MetadataCommitted:
                if (!string.IsNullOrWhiteSpace(claimed.FinalObjectKey))
                {
                    await blobDeletes.PublishAvatarCandidatesAsync(
                            [claimed.FinalObjectKey], claimed.UserId, cancellationToken)
                        .ConfigureAwait(false);
                }

                claimed.Status = AvatarFinalizationSagaStatus.Completed;
                claimed.CompletedAt = DateTimeOffset.UtcNow;
                claimed.UpdatedAt = claimed.CompletedAt.Value;
                if (!await SaveProgressFencedAsync(claimed, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("头像 Finalization Saga 租约已丢失");
                return;

            case AvatarFinalizationSagaStatus.Compensating:
                await CompensateAsync(claimed, claimed.LastError ?? "compensating", cancellationToken)
                    .ConfigureAwait(false);
                return;
        }
    }

    public Task<bool> CompleteClaimedAsync(
        AvatarFinalizationSaga claimed,
        CancellationToken cancellationToken = default)
    {
        if (claimed.Status is not (AvatarFinalizationSagaStatus.Completed
            or AvatarFinalizationSagaStatus.Abandoned
            or AvatarFinalizationSagaStatus.Failed))
            return Task.FromResult(false);

        return ClearLeaseFencedAsync(claimed, cancellationToken);
    }

    public Task<bool> RetryClaimedAsync(
        AvatarFinalizationSaga claimed,
        string error,
        CancellationToken cancellationToken = default)
        => RetryAsync(claimed, error, deadLetter: false, cancellationToken);

    public Task<bool> DeadLetterClaimedAsync(
        AvatarFinalizationSaga claimed,
        string error,
        CancellationToken cancellationToken = default)
        => RetryAsync(claimed, error, deadLetter: true, cancellationToken);

    public async Task<LeaseRenewalResult> RenewLeaseAsync(
        AvatarFinalizationSaga claimed,
        CancellationToken cancellationToken = default)
    {
        var leaseUntil = DateTimeOffset.UtcNow.AddSeconds(
            Math.Clamp(options.Value.FinalizationLeaseSeconds, 30, 900));
        if (!db.Database.IsRelational())
        {
            var tracked = await db.Set<AvatarFinalizationSaga>()
                .SingleOrDefaultAsync(x => x.Id == claimed.Id, cancellationToken)
                .ConfigureAwait(false);
            if (tracked is null
                || !ActiveStatuses.Contains(tracked.Status)
                || tracked.LeaseOwner != claimed.LeaseOwner
                || tracked.LeaseToken != claimed.LeaseToken)
                return LeaseRenewalResult.LeaseLost;

            tracked.LeaseExpiresAt = leaseUntil;
            tracked.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return LeaseRenewalResult.Renewed;
        }

        var updated = await db.Set<AvatarFinalizationSaga>()
            .Where(x => x.Id == claimed.Id
                        && ActiveStatuses.Contains(x.Status)
                        && x.LeaseOwner == claimed.LeaseOwner
                        && x.LeaseToken == claimed.LeaseToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(
                    x => x.LeaseExpiresAt,
                    leaseUntil)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
        return updated == 1 ? LeaseRenewalResult.Renewed : LeaseRenewalResult.LeaseLost;
    }

    private async Task<bool> RetryAsync(
        AvatarFinalizationSaga claimed,
        string error,
        bool deadLetter,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var next = deadLetter
            ? now
            : now.Add(LeasedJobBackoff.ExponentialWithJitter(
                TimeSpan.FromSeconds(5),
                Math.Max(1, claimed.AttemptCount),
                TimeSpan.FromHours(1)));
        var status = deadLetter
            ? AvatarFinalizationSagaStatus.Failed
            : claimed.Status;
        if (!db.Database.IsRelational())
        {
            var tracked = await db.Set<AvatarFinalizationSaga>()
                .SingleOrDefaultAsync(x => x.Id == claimed.Id, cancellationToken)
                .ConfigureAwait(false);
            if (tracked is null
                || !ActiveStatuses.Contains(tracked.Status)
                || tracked.LeaseOwner != claimed.LeaseOwner
                || tracked.LeaseToken != claimed.LeaseToken)
                return false;

            tracked.Status = status;
            tracked.LastError = Truncate(error);
            tracked.NextAttemptAt = next;
            tracked.CompletedAt = deadLetter ? now : null;
            tracked.UpdatedAt = now;
            tracked.LeaseOwner = null;
            tracked.LeaseToken = null;
            tracked.LeaseExpiresAt = null;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (deadLetter)
            {
                logger.LogError(
                    "头像 Finalization Saga 进入死信 SagaId={SagaId} Attempt={Attempt}",
                    claimed.Id,
                    claimed.AttemptCount);
            }
            return true;
        }

        var updated = await db.Set<AvatarFinalizationSaga>()
            .Where(x => x.Id == claimed.Id
                        && ActiveStatuses.Contains(x.Status)
                        && x.LeaseOwner == claimed.LeaseOwner
                        && x.LeaseToken == claimed.LeaseToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status, status)
                    .SetProperty(x => x.LastError, Truncate(error))
                    .SetProperty(x => x.NextAttemptAt, next)
                    .SetProperty(x => x.CompletedAt, deadLetter ? now : (DateTimeOffset?)null)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated == 1 && deadLetter)
            logger.LogError(
                "头像 Finalization Saga 进入死信 SagaId={SagaId} Attempt={Attempt}",
                claimed.Id,
                claimed.AttemptCount);
        return updated == 1;
    }

    private async Task CompensateAsync(
        AvatarFinalizationSaga claimed,
        string reason,
        CancellationToken cancellationToken)
    {
        var keys = new[] { claimed.ObjectKey, claimed.FinalObjectKey }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (keys.Length > 0)
        {
            if (!string.IsNullOrWhiteSpace(claimed.FinalObjectKey))
            {
                // If the candidate tombstone already exists, move that same
                // row back to Pending before the generic enqueue. This avoids
                // creating a second active delete row when compensation races
                // publication/reconciliation.
                await blobDeletes.ReleaseAvatarCandidatesAsync(
                        [claimed.FinalObjectKey], claimed.UserId, cancellationToken)
                    .ConfigureAwait(false);
            }

            await blobDeletes.EnqueueAvatarAsync(
                    keys, claimed.UserId, cancellationToken)
                .ConfigureAwait(false);
        }

        claimed.Status = AvatarFinalizationSagaStatus.Abandoned;
        claimed.LastError = Truncate(reason);
        claimed.CompletedAt = DateTimeOffset.UtcNow;
        claimed.UpdatedAt = claimed.CompletedAt.Value;
        if (!await SaveProgressFencedAsync(claimed, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("头像 Finalization Saga 租约已丢失");
    }

    private async Task<bool> SaveProgressFencedAsync(
        AvatarFinalizationSaga saga,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational())
        {
            var tracked = await db.Set<AvatarFinalizationSaga>()
                .SingleOrDefaultAsync(x => x.Id == saga.Id, cancellationToken)
                .ConfigureAwait(false);
            if (tracked is null
                || tracked.LeaseOwner != saga.LeaseOwner
                || tracked.LeaseToken != saga.LeaseToken)
                return false;

            tracked.Status = saga.Status;
            tracked.ProtectedTicket = saga.ProtectedTicket;
            tracked.FinalObjectKey = saga.FinalObjectKey;
            tracked.PublicUrl = saga.PublicUrl;
            tracked.AttemptCount = saga.AttemptCount;
            tracked.NextAttemptAt = saga.NextAttemptAt;
            tracked.UpdatedAt = saga.UpdatedAt;
            tracked.CompletedAt = saga.CompletedAt;
            tracked.LastError = saga.LastError;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var updated = await db.Set<AvatarFinalizationSaga>()
            .Where(x => x.Id == saga.Id
                        && x.LeaseOwner == saga.LeaseOwner
                        && x.LeaseToken == saga.LeaseToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status, saga.Status)
                    .SetProperty(x => x.ProtectedTicket, saga.ProtectedTicket)
                    .SetProperty(x => x.FinalObjectKey, saga.FinalObjectKey)
                    .SetProperty(x => x.PublicUrl, saga.PublicUrl)
                    .SetProperty(x => x.AttemptCount, saga.AttemptCount)
                    .SetProperty(x => x.NextAttemptAt, saga.NextAttemptAt)
                    .SetProperty(x => x.UpdatedAt, saga.UpdatedAt)
                    .SetProperty(x => x.CompletedAt, saga.CompletedAt)
                    .SetProperty(x => x.LastError, saga.LastError),
                cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    private async Task<bool> ClearLeaseFencedAsync(
        AvatarFinalizationSaga saga,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational())
        {
            var tracked = await db.Set<AvatarFinalizationSaga>()
                .SingleOrDefaultAsync(x => x.Id == saga.Id, cancellationToken)
                .ConfigureAwait(false);
            if (tracked is null
                || tracked.LeaseOwner != saga.LeaseOwner
                || tracked.LeaseToken != saga.LeaseToken)
                return false;

            tracked.LeaseOwner = null;
            tracked.LeaseToken = null;
            tracked.LeaseExpiresAt = null;
            tracked.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var updated = await db.Set<AvatarFinalizationSaga>()
            .Where(x => x.Id == saga.Id
                        && x.LeaseOwner == saga.LeaseOwner
                        && x.LeaseToken == saga.LeaseToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    private async Task<bool> IsLeaseStillOwnedAsync(
        AvatarFinalizationSaga saga,
        CancellationToken cancellationToken)
        => await db.Set<AvatarFinalizationSaga>().AsNoTracking()
            .AnyAsync(
                x => x.Id == saga.Id
                     && x.LeaseOwner == saga.LeaseOwner
                     && x.LeaseToken == saga.LeaseToken
                     && x.LeaseExpiresAt > DateTimeOffset.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<int> CommitAvatarMetadataAsync(
        AvatarFinalizationSaga saga,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational())
        {
            var tracked = await db.Users
                .SingleOrDefaultAsync(x => x.Id == saga.UserId, cancellationToken)
                .ConfigureAwait(false);
            if (tracked is null
                || tracked.AvatarVersion != saga.ExpectedAvatarVersion
                || tracked.DeletionEpoch != saga.UploaderDeletionEpoch
                || tracked.DeletionScheduledAt is not null
                || tracked.AccountState is AccountState.DeletionPending or AccountState.Deleted)
                return 0;

            tracked.AvatarUrl = saga.PublicUrl;
            tracked.AvatarVersion = saga.ExpectedAvatarVersion + 1;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return 1;
        }

        return await db.Users
            .Where(x => x.Id == saga.UserId
                        && x.AvatarVersion == saga.ExpectedAvatarVersion
                        && x.DeletionEpoch == saga.UploaderDeletionEpoch
                        && x.DeletionScheduledAt == null
                        && x.AccountState == AccountState.Active)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.AvatarUrl, saga.PublicUrl)
                    .SetProperty(x => x.AvatarVersion, saga.ExpectedAvatarVersion + 1),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsOwnedObjectKey(long userId, string objectKey)
        => objectKey.StartsWith($"avatars/{userId}/", StringComparison.Ordinal)
           || objectKey.StartsWith($"{userId}/", StringComparison.Ordinal);

    private bool OwnsLease(AvatarFinalizationSaga saga)
        => !string.IsNullOrWhiteSpace(saga.LeaseOwner)
           && !string.IsNullOrWhiteSpace(saga.LeaseToken)
           && saga.LeaseExpiresAt > DateTimeOffset.UtcNow;

    private string? UnprotectTicket(string? protectedTicket)
        => string.IsNullOrWhiteSpace(protectedTicket)
            ? null
            : ticketProtector.Unprotect(protectedTicket);

    private static AvatarFinalizationStatusDto ToResponse(AvatarFinalizationSaga saga)
        => new(
            saga.Id,
            saga.ObjectKey,
            saga.PublicUrl,
            saga.Status,
            saga.CreatedAt,
            saga.UpdatedAt,
            string.IsNullOrWhiteSpace(saga.LastError) ? null : "avatar_finalization_failed");

    private static string Truncate(string value)
        => value.Length <= 500 ? value : value[..500];

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
