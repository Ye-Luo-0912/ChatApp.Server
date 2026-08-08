using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public sealed class AttachmentBlobDeleteService(
    UserDbContext db,
    IAttachmentStorage storage,
    IOptions<AttachmentStorageOptions> options,
    ILogger<AttachmentBlobDeleteService> logger,
    IAvatarStorage? avatarStorage = null) : IAttachmentBlobDeleteService
{
    private readonly IAvatarStorage? _avatarStorage = avatarStorage;
    private static readonly string ProcessOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}";
    private static readonly string[] ActiveStatuses =
    [
        AttachmentBlobDeleteJobStatus.Pending,
        AttachmentBlobDeleteJobStatus.AwaitingPublication,
        AttachmentBlobDeleteJobStatus.Processing,
    ];
    private static readonly TimeSpan AvatarPublicationGrace = TimeSpan.FromHours(1);

    /// <summary>删除通常是短 I/O；实例崩溃后可安全接管。</summary>
    public const int LeaseMinutes = 5;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(LeaseMinutes);

    public Task EnqueueAsync(
        IEnumerable<string> objectKeys,
        long? userId = null,
        string? attachmentId = null,
        CancellationToken cancellationToken = default)
        => EnqueueCoreAsync(
            objectKeys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => (k.Trim(), attachmentId)),
            userId,
            AttachmentBlobDeleteStorageKind.Attachment,
            cancellationToken);

    public async Task EnqueueAsync(
        IEnumerable<(string ObjectKey, string? AttachmentId)> items,
        long? userId = null,
        CancellationToken cancellationToken = default)
        => await EnqueueCoreAsync(
                items,
                userId,
                AttachmentBlobDeleteStorageKind.Attachment,
                cancellationToken)
            .ConfigureAwait(false);

    public Task EnqueueAvatarAsync(
        IEnumerable<string> objectKeys,
        long? userId = null,
        CancellationToken cancellationToken = default)
        => EnqueueCoreAsync(
            objectKeys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => (k.Trim(), (string?)null)),
            userId,
            AttachmentBlobDeleteStorageKind.Avatar,
            cancellationToken);

    public Task EnqueueAvatarCandidatesAsync(
        IEnumerable<string> objectKeys,
        long? userId = null,
        CancellationToken cancellationToken = default)
        => EnqueueCoreAsync(
            objectKeys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => (k.Trim(), (string?)null)),
            userId,
            AttachmentBlobDeleteStorageKind.Avatar,
            cancellationToken,
            AttachmentBlobDeleteJobStatus.AwaitingPublication,
            DateTimeOffset.UtcNow.Add(AvatarPublicationGrace));

    public async Task PublishAvatarCandidatesAsync(
        IEnumerable<string> objectKeys,
        long? userId = null,
        CancellationToken cancellationToken = default)
    {
        var keys = objectKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (keys.Length == 0)
            return;

        var query = db.AttachmentBlobDeleteJobs
            .Where(j => keys.Contains(j.ObjectKey)
                        && j.StorageKind == AttachmentBlobDeleteStorageKind.Avatar
                        && (j.Status == AttachmentBlobDeleteJobStatus.AwaitingPublication
                            || j.Status == AttachmentBlobDeleteJobStatus.Published));
        if (userId is { } id)
            query = query.Where(j => j.UserId == id);

        // S3 avatar objects are deliberately written with the unconfirmed
        // lifecycle tag. Promote the object before changing the durable
        // candidate row to Published. If this call or the surrounding user
        // transaction fails, the AwaitingPublication tombstone remains a
        // safe deletion candidate and no confirmed object can be reclaimed.
        if (_avatarStorage is IAvatarPublicationStorage publicationStorage)
        {
            foreach (var key in keys)
                await publicationStorage.PublishAsync(key, cancellationToken)
                    .ConfigureAwait(false);
        }

        var now = DateTimeOffset.UtcNow;
        var published = await query.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(j => j.Status, AttachmentBlobDeleteJobStatus.Published)
                    .SetProperty(j => j.CompletedAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.LastError, (string?)null)
                    .SetProperty(j => j.NextAttemptAt, now.Add(AvatarPublicationGrace))
                    .SetProperty(j => j.LeaseOwner, (string?)null)
                    .SetProperty(j => j.LeaseToken, (string?)null)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null),
                cancellationToken)
            .ConfigureAwait(false);
        if (published > 0)
            AuthSecurityMetrics.AttachmentPendingDeleteDelta(-published);
    }

    public async Task ReleaseAvatarCandidatesAsync(
        IEnumerable<string> objectKeys,
        long? userId = null,
        CancellationToken cancellationToken = default)
    {
        var keys = objectKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (keys.Length == 0)
            return;

        var query = db.AttachmentBlobDeleteJobs
            .Where(j => keys.Contains(j.ObjectKey)
                        && j.StorageKind == AttachmentBlobDeleteStorageKind.Avatar
                        && j.Status == AttachmentBlobDeleteJobStatus.AwaitingPublication);
        if (userId is { } id)
            query = query.Where(j => j.UserId == id);

        await query.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(j => j.Status, AttachmentBlobDeleteJobStatus.Pending)
                    .SetProperty(j => j.NextAttemptAt, DateTimeOffset.UtcNow)
                    .SetProperty(j => j.LastError, (string?)null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task EnqueueCoreAsync(
        IEnumerable<(string ObjectKey, string? AttachmentId)> items,
        long? userId,
        string storageKind,
        CancellationToken cancellationToken,
        string initialStatus = AttachmentBlobDeleteJobStatus.Pending,
        DateTimeOffset? initialNextAttemptAt = null)
    {
        var normalized = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (objectKey, attachmentId) in items)
        {
            if (!string.IsNullOrWhiteSpace(objectKey))
                normalized.TryAdd(objectKey.Trim(), attachmentId);
        }

        if (normalized.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var objectKeys = normalized.Keys.ToArray();
        // Pending/processing/candidate rows are the active idempotency
        // boundary. A Published avatar is intentionally excluded: it is a
        // retention record, and the same object key may need a new deletion
        // tombstone after the user replaces that avatar.
        var existingRows = await db.AttachmentBlobDeleteJobs
            .AsNoTracking()
            .Where(j => objectKeys.Contains(j.ObjectKey))
            .Select(j => new { j.ObjectKey, j.StorageKind, j.Status, j.AttachmentId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var existing = existingRows
            .Where(row =>
            {
                if (row.Status is AttachmentBlobDeleteJobStatus.Pending
                    or AttachmentBlobDeleteJobStatus.AwaitingPublication
                    or AttachmentBlobDeleteJobStatus.Processing)
                    return true;

                // Repeated age sweeps must not create a fresh tombstone after
                // the previous attachment deletion reached Done/DeadLetter.
                // A terminal row is scoped by AttachmentId so a broken legacy
                // key reuse cannot suppress a different attachment. Avatar
                // final keys are deterministic and terminal deletion is also
                // final, so they are safe to suppress by object key.
                if (row.Status is not (AttachmentBlobDeleteJobStatus.Done
                    or AttachmentBlobDeleteJobStatus.DeadLetter))
                    return false;

                if (string.Equals(storageKind, AttachmentBlobDeleteStorageKind.Avatar,
                        StringComparison.Ordinal))
                    return true;

                return row.AttachmentId is not null
                       && normalized.TryGetValue(row.ObjectKey, out var suppliedAttachmentId)
                       && string.Equals(row.AttachmentId, suppliedAttachmentId, StringComparison.Ordinal);
            })
            .Select(row => row.ObjectKey)
            .ToHashSet(StringComparer.Ordinal);
        var pending = normalized
            .Where(pair => !existing.Contains(pair.Key))
            .Select(pair => (pair.Key, pair.Value))
            .ToArray();
        if (pending.Length == 0)
            return;

        var nextAttemptAt = initialNextAttemptAt ?? now;
        var queued = IsNpgsql()
            ? await EnqueueNpgsqlAsync(
                    pending, userId, storageKind, initialStatus, nextAttemptAt, now, cancellationToken)
                .ConfigureAwait(false)
            : await EnqueueFallbackAsync(
                    pending, userId, storageKind, initialStatus, nextAttemptAt, now, cancellationToken)
                .ConfigureAwait(false);
        if (queued == 0)
            return;

        AuthSecurityMetrics.AttachmentPendingDeleteDelta(queued);
        logger.LogInformation(
            "已入队 {Count} 条附件 blob 删除墓碑 UserId={UserId}",
            queued,
            userId);
    }

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var batchSize = Math.Clamp(options.Value.DeleteBatchSize, 1, 500);
        var claimed = await ClaimDueJobsAsync(batchSize, cancellationToken).ConfigureAwait(false);
        var deleted = 0;
        foreach (var job in claimed)
        {
            if (await ProcessClaimedJobAsync(job, cancellationToken).ConfigureAwait(false))
                deleted++;
        }

        return deleted;
    }

    /// <summary>
    /// 原子领取到期或租约过期的墓碑。生产 PostgreSQL 使用 <c>FOR UPDATE SKIP LOCKED</c>，
    /// 每个领取批次都有独立 owner/token，旧 Worker 无法覆盖新的持有者。
    /// </summary>
    public async Task<IReadOnlyList<AttachmentBlobDeleteJob>> ClaimDueJobsAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(Lease);
        var owner = $"{ProcessOwner}:{Guid.NewGuid():N}";
        if (owner.Length > 128)
            owner = owner[..128];

        // Candidate rows are deliberately not claimed as deletion jobs until
        // their owning user row has been checked. This closes the crash window
        // between the avatar transaction and candidate publication: a current
        // avatar becomes Published, while an unreferenced candidate becomes a
        // normal Pending deletion tombstone.
        await ReconcileAvatarCandidatesAsync(now, cancellationToken)
            .ConfigureAwait(false);

        if (IsNpgsql())
        {
            var claimedIds = await ClaimDueJobIdsNpgsqlAsync(
                    batchSize, owner, now, leaseUntil, cancellationToken)
                .ConfigureAwait(false);
            if (claimedIds.Count == 0)
                return [];

            return await db.AttachmentBlobDeleteJobs
                .AsNoTracking()
                .Where(j => claimedIds.Contains(j.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        // InMemory/SQLite 测试回退：生产路径不依赖此实现。
        var due = await db.AttachmentBlobDeleteJobs
            .Where(j =>
                (j.Status == AttachmentBlobDeleteJobStatus.Pending && j.NextAttemptAt <= now)
                || (j.Status == AttachmentBlobDeleteJobStatus.Processing
                    && j.LeaseExpiresAt != null
                    && j.LeaseExpiresAt < now))
            .OrderBy(j => j.NextAttemptAt)
            .ThenBy(j => j.Id)
            .Take(Math.Clamp(batchSize, 1, 500))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (due.Count == 0)
            return due;

        foreach (var job in due)
        {
            job.Status = AttachmentBlobDeleteJobStatus.Processing;
            job.LeaseOwner = owner;
            job.LeaseToken = Guid.NewGuid().ToString("N");
            job.LeaseExpiresAt = leaseUntil;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return due;
    }

    /// <summary>仅由当前 owner/token 持有者续租。</summary>
    public async Task<LeaseRenewalResult> RenewLeaseAsync(
        AttachmentBlobDeleteJob claimed,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(claimed.LeaseOwner)
            || string.IsNullOrWhiteSpace(claimed.LeaseToken))
            return LeaseRenewalResult.LeaseLost;

        var until = DateTimeOffset.UtcNow.Add(Lease);
        try
        {
            if (!IsNpgsql())
            {
                var tracked = await db.AttachmentBlobDeleteJobs
                    .FirstOrDefaultAsync(j => j.Id == claimed.Id
                        && j.Status == AttachmentBlobDeleteJobStatus.Processing
                        && j.LeaseOwner == claimed.LeaseOwner
                        && j.LeaseToken == claimed.LeaseToken, cancellationToken)
                    .ConfigureAwait(false);
                if (tracked is null)
                    return LeaseRenewalResult.LeaseLost;

                tracked.LeaseExpiresAt = until;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return LeaseRenewalResult.Renewed;
            }

            var updated = await db.AttachmentBlobDeleteJobs
                .Where(j => j.Id == claimed.Id
                    && j.Status == AttachmentBlobDeleteJobStatus.Processing
                    && j.LeaseOwner == claimed.LeaseOwner
                    && j.LeaseToken == claimed.LeaseToken)
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
        catch (Exception ex)
        {
            logger.LogDebug(ex, "附件 blob 删除租约续租失败 JobId={Id}", claimed.Id);
            return LeaseRenewalResult.TransientFailure;
        }
    }

    /// <summary>删除成功后的 fenced 终态写入。</summary>
    public async Task<bool> CompleteClaimedJobAsync(
        AttachmentBlobDeleteJob claimed,
        CancellationToken cancellationToken = default)
    {
        var completed = await ApplyFencedUpdateAsync(
                claimed,
                new TargetFields
                {
                    Status = AttachmentBlobDeleteJobStatus.Done,
                    AttemptCount = claimed.AttemptCount,
                    CompletedAt = DateTimeOffset.UtcNow,
                    LastError = null,
                    NextAttemptAt = claimed.NextAttemptAt,
                    LeaseOwner = null,
                    LeaseToken = null,
                    LeaseExpiresAt = null,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!completed)
            return false;

        AuthSecurityMetrics.AttachmentBlobDelete("success");
        AuthSecurityMetrics.AttachmentPendingDeleteDelta(-1);
        return true;
    }

    /// <summary>失败后的可重试状态写入；返回 false 表示租约已易主。</summary>
    public async Task<bool> RetryClaimedJobAsync(
        AttachmentBlobDeleteJob claimed,
        string error,
        CancellationToken cancellationToken = default)
    {
        var attemptCount = Math.Max(1, claimed.AttemptCount + 1);
        var opts = options.Value;
        var updated = await ApplyFencedUpdateAsync(
                claimed,
                new TargetFields
                {
                    Status = AttachmentBlobDeleteJobStatus.Pending,
                    AttemptCount = attemptCount,
                    CompletedAt = null,
                    LastError = Truncate(error, 500),
                    NextAttemptAt = DateTimeOffset.UtcNow.Add(ComputeBackoff(opts, attemptCount)),
                    LeaseOwner = null,
                    LeaseToken = null,
                    LeaseExpiresAt = null,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!updated)
            return false;

        AuthSecurityMetrics.AttachmentBlobDelete("failed");
        logger.LogWarning(
            "附件 blob 删除失败，已按退避重新入队 JobId={Id} Key={Key} Attempt={Attempt}",
            claimed.Id,
            claimed.ObjectKey,
            attemptCount);
        return true;
    }

    /// <summary>重试耗尽后的 DeadLetter fenced 状态写入。</summary>
    public async Task<bool> DeadLetterClaimedJobAsync(
        AttachmentBlobDeleteJob claimed,
        string error,
        CancellationToken cancellationToken = default)
    {
        var attemptCount = Math.Max(1, claimed.AttemptCount + 1);
        var updated = await ApplyFencedUpdateAsync(
                claimed,
                new TargetFields
                {
                    Status = AttachmentBlobDeleteJobStatus.DeadLetter,
                    AttemptCount = attemptCount,
                    CompletedAt = DateTimeOffset.UtcNow,
                    LastError = Truncate(error, 500),
                    NextAttemptAt = DateTimeOffset.UtcNow,
                    LeaseOwner = null,
                    LeaseToken = null,
                    LeaseExpiresAt = null,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!updated)
            return false;

        AuthSecurityMetrics.AttachmentBlobDelete("failed");
        AuthSecurityMetrics.AttachmentBlobDelete("exhausted");
        AuthSecurityMetrics.AttachmentPendingDeleteDelta(-1);
        logger.LogError(
            "附件 blob 删除重试已耗尽，转入 DeadLetter JobId={Id} Key={Key} Attempts={Attempt}",
            claimed.Id,
            claimed.ObjectKey,
            attemptCount);
        return true;
    }

    /// <summary>
    /// 外部删除完成后仅由仍持有同一租约的实例更新数据库。重试耗尽后进入
    /// DeadLetter，而不是继续保留 Pending 并无限重试。
    /// </summary>
    public async Task<bool> ProcessClaimedJobAsync(
        AttachmentBlobDeleteJob claimed,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(claimed.LeaseOwner)
            || string.IsNullOrWhiteSpace(claimed.LeaseToken))
            return false;

        var opts = options.Value;
        var maxAttempts = Math.Max(1, opts.MaxDeleteAttempts);
        try
        {
            await DeleteObjectAsync(claimed, cancellationToken).ConfigureAwait(false);
            var completed = await ApplyFencedUpdateAsync(
                    claimed,
                    new TargetFields
                    {
                        Status = AttachmentBlobDeleteJobStatus.Done,
                        AttemptCount = claimed.AttemptCount,
                        CompletedAt = DateTimeOffset.UtcNow,
                        LastError = null,
                        NextAttemptAt = claimed.NextAttemptAt,
                        LeaseOwner = null,
                        LeaseToken = null,
                        LeaseExpiresAt = null,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!completed)
            {
                logger.LogInformation(
                    "附件 blob 删除已完成但租约已易主，丢弃旧结果 JobId={Id} Key={Key}",
                    claimed.Id,
                    claimed.ObjectKey);
                return false;
            }

            AuthSecurityMetrics.AttachmentBlobDelete("success");
            AuthSecurityMetrics.AttachmentPendingDeleteDelta(-1);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var attemptCount = Math.Max(1, claimed.AttemptCount + 1);
            var exhausted = attemptCount >= maxAttempts;
            var updated = await ApplyFencedUpdateAsync(
                    claimed,
                    new TargetFields
                    {
                        Status = exhausted
                            ? AttachmentBlobDeleteJobStatus.DeadLetter
                            : AttachmentBlobDeleteJobStatus.Pending,
                        AttemptCount = attemptCount,
                        CompletedAt = exhausted ? DateTimeOffset.UtcNow : null,
                        LastError = Truncate(ex.Message, 500),
                        NextAttemptAt = exhausted
                            ? DateTimeOffset.UtcNow
                            : DateTimeOffset.UtcNow.Add(ComputeBackoff(opts, attemptCount)),
                        LeaseOwner = null,
                        LeaseToken = null,
                        LeaseExpiresAt = null,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!updated)
                return false;

            AuthSecurityMetrics.AttachmentBlobDelete("failed");
            if (exhausted)
            {
                AuthSecurityMetrics.AttachmentBlobDelete("exhausted");
                AuthSecurityMetrics.AttachmentPendingDeleteDelta(-1);
                logger.LogError(
                    ex,
                    "附件 blob 删除重试已耗尽，转入 DeadLetter JobId={Id} Key={Key} Attempts={Attempt}",
                    claimed.Id,
                    claimed.ObjectKey,
                    attemptCount);
            }
            else
            {
                logger.LogWarning(
                    ex,
                    "附件 blob 删除失败，已按退避重新入队 JobId={Id} Key={Key} Attempt={Attempt}",
                    claimed.Id,
                    claimed.ObjectKey,
                    attemptCount);
            }

            return false;
        }
    }

    private async Task<int> EnqueueFallbackAsync(
        IReadOnlyList<(string ObjectKey, string? AttachmentId)> items,
        long? userId,
        string storageKind,
        string status,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        db.AttachmentBlobDeleteJobs.AddRange(items.Select(item => new AttachmentBlobDeleteJob
        {
            ObjectKey = item.ObjectKey,
            StorageKind = storageKind,
            AttachmentId = item.AttachmentId,
            UserId = userId,
            Status = status,
            AttemptCount = 0,
            NextAttemptAt = nextAttemptAt,
            CreatedAt = now,
        }));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return items.Count;
    }

    private async Task ReconcileAvatarCandidatesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidates = await db.AttachmentBlobDeleteJobs
            .AsNoTracking()
            .Where(j => (j.Status == AttachmentBlobDeleteJobStatus.AwaitingPublication
                         || j.Status == AttachmentBlobDeleteJobStatus.Published)
                        && j.NextAttemptAt <= now)
            .OrderBy(j => j.NextAttemptAt)
            .ThenBy(j => j.Id)
            .Take(500)
            .Select(j => new { j.Id, j.ObjectKey, j.UserId, j.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
            return;

        var userIds = candidates
            .Where(x => x.UserId.HasValue)
            .Select(x => x.UserId!.Value)
            .Distinct()
            .ToArray();
        var avatarUrls = userIds.Length == 0
            ? []
            : await db.Users.AsNoTracking()
                .Where(user => userIds.Contains(user.Id))
                .Select(user => new { user.Id, user.AvatarUrl })
                .ToDictionaryAsync(x => x.Id, x => x.AvatarUrl, cancellationToken)
                .ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            avatarUrls.TryGetValue(candidate.UserId ?? 0, out var avatarUrl);
            var stillReferenced = ReferencesAvatar(avatarUrl, candidate.ObjectKey);
            if (stillReferenced)
            {
                if (candidate.Status == AttachmentBlobDeleteJobStatus.AwaitingPublication
                    && _avatarStorage is IAvatarPublicationStorage publicationStorage)
                {
                    // Reconciliation is the crash-recovery leg of the avatar
                    // finalization saga. A successful CAS/reference check is
                    // the authority for promoting the candidate tag.
                    await publicationStorage.PublishAsync(
                            candidate.ObjectKey,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (candidate.Status == AttachmentBlobDeleteJobStatus.AwaitingPublication)
                {
                    var published = await db.AttachmentBlobDeleteJobs
                        .Where(job => job.Id == candidate.Id
                                      && job.Status == AttachmentBlobDeleteJobStatus.AwaitingPublication)
                        .ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(job => job.Status, AttachmentBlobDeleteJobStatus.Published)
                                .SetProperty(job => job.CompletedAt, (DateTimeOffset?)null)
                                .SetProperty(job => job.LastError, (string?)null)
                                .SetProperty(job => job.NextAttemptAt, DateTimeOffset.MaxValue),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (published == 1)
                        AuthSecurityMetrics.AttachmentPendingDeleteDelta(-1);
                }

                continue;
            }

            var pending = await db.AttachmentBlobDeleteJobs
                .Where(job => job.Id == candidate.Id
                              && (job.Status == AttachmentBlobDeleteJobStatus.AwaitingPublication
                                  || job.Status == AttachmentBlobDeleteJobStatus.Published))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(job => job.Status, AttachmentBlobDeleteJobStatus.Pending)
                        .SetProperty(job => job.CompletedAt, (DateTimeOffset?)null)
                        .SetProperty(job => job.LastError, (string?)null)
                        .SetProperty(job => job.NextAttemptAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
            if (pending == 1 && candidate.Status == AttachmentBlobDeleteJobStatus.Published)
                AuthSecurityMetrics.AttachmentPendingDeleteDelta(1);
        }
    }

    private static bool ReferencesAvatar(string? avatarUrl, string objectKey)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl) || string.IsNullOrWhiteSpace(objectKey))
            return false;

        var normalizedKey = objectKey.TrimStart('/');
        return string.Equals(avatarUrl, normalizedKey, StringComparison.Ordinal)
               || avatarUrl.EndsWith('/' + normalizedKey, StringComparison.Ordinal);
    }

    private async Task<int> EnqueueNpgsqlAsync(
        IReadOnlyList<(string ObjectKey, string? AttachmentId)> items,
        long? userId,
        string storageKind,
        string status,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();
        command.CommandText =
            """
            INSERT INTO "T_AttachmentBlobDeleteJob"
                ("ObjectKey", "StorageKind", "AttachmentId", "UserId", "Status", "AttemptCount", "NextAttemptAt", "CreatedAt")
            SELECT i."ObjectKey", @storage_kind, i."AttachmentId", @user_id::bigint, @status, 0, @next_attempt_at, @now
            FROM unnest(@object_keys::text[], @attachment_ids::text[])
                AS i("ObjectKey", "AttachmentId")
            ON CONFLICT DO NOTHING
            RETURNING "Id";
            """;
        AddParameter(command, "user_id", userId.HasValue ? (object)userId.Value : DBNull.Value);
        AddParameter(command, "storage_kind", storageKind);
        AddParameter(command, "status", status);
        AddParameter(command, "next_attempt_at", nextAttemptAt);
        AddParameter(command, "now", now);
        AddParameter(command, "object_keys", items.Select(item => item.ObjectKey).ToArray());
        AddParameter(command, "attachment_ids", items.Select(item => item.AttachmentId).ToArray());

        var queued = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            queued++;
        return queued;
    }

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
        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();
        command.CommandText =
            """
            UPDATE "T_AttachmentBlobDeleteJob" AS j
            SET "Status" = 'Processing',
                "LeaseOwner" = @owner,
                "LeaseToken" = md5(random()::text || clock_timestamp()::text || j."Id"::text),
                "LeaseExpiresAt" = @lease_until
            WHERE j."Id" IN (
                SELECT c."Id"
                FROM "T_AttachmentBlobDeleteJob" AS c
             WHERE (c."Status" = 'Pending' AND c."NextAttemptAt" <= @now)
                   OR (c."Status" = 'Processing'
                       AND c."LeaseExpiresAt" IS NOT NULL
                       AND c."LeaseExpiresAt" < @now)
                ORDER BY c."NextAttemptAt", c."Id"
                FOR UPDATE SKIP LOCKED
                LIMIT @batch
            )
            RETURNING j."Id";
        """;
        AddParameter(command, "owner", owner);
        AddParameter(command, "lease_until", leaseUntil);
        AddParameter(command, "now", now);
        AddParameter(command, "batch", Math.Clamp(batchSize, 1, 500));

        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private bool IsNpgsql() =>
        db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    private sealed class TargetFields
    {
        public string Status { get; init; } = string.Empty;
        public int AttemptCount { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public string? LastError { get; init; }
        public DateTimeOffset NextAttemptAt { get; init; }
        public string? LeaseOwner { get; init; }
        public string? LeaseToken { get; init; }
        public DateTimeOffset? LeaseExpiresAt { get; init; }
    }

    private async Task<bool> ApplyFencedUpdateAsync(
        AttachmentBlobDeleteJob claimed,
        TargetFields target,
        CancellationToken cancellationToken)
    {
        if (!IsNpgsql())
        {
            var tracked = await db.AttachmentBlobDeleteJobs
                .FirstOrDefaultAsync(j => j.Id == claimed.Id, cancellationToken)
                .ConfigureAwait(false);
            if (tracked is null
                || tracked.Status != AttachmentBlobDeleteJobStatus.Processing
                || tracked.LeaseOwner != claimed.LeaseOwner
                || tracked.LeaseToken != claimed.LeaseToken)
                return false;

            tracked.Status = target.Status;
            tracked.AttemptCount = target.AttemptCount;
            tracked.CompletedAt = target.CompletedAt;
            tracked.LastError = target.LastError;
            tracked.NextAttemptAt = target.NextAttemptAt;
            tracked.LeaseOwner = target.LeaseOwner;
            tracked.LeaseToken = target.LeaseToken;
            tracked.LeaseExpiresAt = target.LeaseExpiresAt;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var updated = await db.AttachmentBlobDeleteJobs
            .Where(j => j.Id == claimed.Id
                && j.Status == AttachmentBlobDeleteJobStatus.Processing
                && j.LeaseOwner == claimed.LeaseOwner
                && j.LeaseToken == claimed.LeaseToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, target.Status)
                    .SetProperty(j => j.AttemptCount, target.AttemptCount)
                    .SetProperty(j => j.CompletedAt, target.CompletedAt)
                    .SetProperty(j => j.LastError, target.LastError)
                    .SetProperty(j => j.NextAttemptAt, target.NextAttemptAt)
                    .SetProperty(j => j.LeaseOwner, target.LeaseOwner)
                    .SetProperty(j => j.LeaseToken, target.LeaseToken)
                    .SetProperty(j => j.LeaseExpiresAt, target.LeaseExpiresAt),
                cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    private static TimeSpan ComputeBackoff(AttachmentStorageOptions opts, int attemptCount)
        => LeasedJobBackoff.ExponentialWithJitter(
            TimeSpan.FromSeconds(Math.Max(5, opts.DeleteBackoffSeconds)),
            attemptCount,
            TimeSpan.FromHours(1));

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    private Task DeleteObjectAsync(
        AttachmentBlobDeleteJob job,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
                job.StorageKind,
                AttachmentBlobDeleteStorageKind.Avatar,
                StringComparison.Ordinal))
        {
            if (_avatarStorage is null)
                throw new InvalidOperationException("头像删除存储未注册");

            return _avatarStorage.TryDeleteAsync(job.ObjectKey, cancellationToken);
        }

        return storage.DeleteAsync(job.ObjectKey, cancellationToken);
    }
}

/// <summary>DI 作用域工厂封装，供 BackgroundService 入队。</summary>
public sealed class AttachmentBlobDeleteEnqueuer(IServiceScopeFactory scopeFactory) 
{
    public async Task EnqueueAsync(
        IEnumerable<string> objectKeys,
        long? userId = null,
        string? attachmentId = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAttachmentBlobDeleteService>();
        await svc.EnqueueAsync(objectKeys, userId, attachmentId, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnqueueAsync(
        IEnumerable<(string ObjectKey, string? AttachmentId)> items,
        long? userId = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAttachmentBlobDeleteService>();
        await svc.EnqueueAsync(items, userId, cancellationToken).ConfigureAwait(false);
    }
}
