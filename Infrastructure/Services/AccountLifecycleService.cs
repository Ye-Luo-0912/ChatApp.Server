using ChatApp.Realtime.Abstractions.Events;
using ChatApp.Realtime.Integration.Outbox;
using ChatApp.Realtime.Integration.Serialization;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models.Auth;
using Core.Models.Common;
using Core.Models.Export;
using Core.Models.Identity;
using Core.Models.Security;
using Core.Settings;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Infrastructure.Services.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public sealed class AccountLifecycleService(
    UserDbContext db,
    ISessionStore sessionStore,
    ISecurityEventStore securityEventStore,
    IDataExportBlobStore dataExportBlobs,
    IAttachmentMetadataStore attachmentMetadata,
    IAttachmentBlobDeleteService attachmentBlobDeletes,
    ILogger<AccountLifecycleService> logger,
    ISecurityVersionAdvancer? securityVersions = null,
    IServiceScopeFactory? scopeFactory = null,
    ISecurityMutationCoordinator? securityMutations = null) : IAccountLifecycleService
{
    public static readonly TimeSpan CoolDown = AuthTimingDefaults.AccountDeletionCooldown;
    private readonly ISecurityMutationCoordinator _securityMutationCoordinator =
        securityMutations ?? new SecurityMutationCoordinator(
            db,
            securityVersions ?? new SecurityVersionAdvancer(db),
            NullLogger<SecurityMutationCoordinator>.Instance);
    private readonly IServiceScopeFactory? _scopeFactory = scopeFactory;
    private readonly string _instanceId = Environment.MachineName + ":" + Guid.NewGuid().ToString("N")[..8];
    public static readonly TimeSpan DeletionLeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>测试钩子：领取租约后、逐用户清理前调用（用于取消竞态注入）。</summary>
    public Func<IReadOnlyList<long>, CancellationToken, Task>? AfterClaimHook { get; set; }

    public string LeaseOwnerId => _instanceId;

    public Task<AuthOperationResult> ScheduleDeletionAsync(
        long userId, CancellationToken cancellationToken = default) =>
        ScheduleDeletionCoreAsync(
            userId, actorUserId: null, reason: null, clientIp: null, cancellationToken);

    public Task<AuthOperationResult> ScheduleDeletionByAdminAsync(
        long userId,
        long actorUserId,
        string? reason,
        string? clientIp,
        CancellationToken cancellationToken = default) =>
        ScheduleDeletionCoreAsync(userId, actorUserId, reason, clientIp, cancellationToken);

    private async Task<AuthOperationResult> ScheduleDeletionCoreAsync(
        long userId,
        long? actorUserId,
        string? reason,
        string? clientIp,
        CancellationToken cancellationToken)
    {
        // Kept in the constructor for compatibility with direct worker tests;
        // security events now flow through the mutation coordinator so they
        // share the same transaction as the user fence.
        _ = securityEventStore;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await AdminRoleInvariant.AcquireMutationLockAsync(db, cancellationToken);
        if (db.Database.ProviderName?.Contains(
                "Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {userId} FOR UPDATE""",
                cancellationToken);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return AuthOperationResult.Fail("NotFound", "用户不存在");
        }

        if (await AdminRoleInvariant.IsLastActiveAdminAsync(db, userId, cancellationToken))
        {
            await tx.RollbackAsync(cancellationToken);
            return AuthOperationResult.Fail("LastAdmin", "不能注销最后一个可用管理员");
        }

        user.DeletionScheduledAt = DateTimeOffset.UtcNow.Add(CoolDown);
        user.AccountState = AccountState.DeletionPending;
        user.DeletionEpoch = NextDeletionEpoch(user.DeletionEpoch);
        user.DeletionLeaseUntil = null;
        user.DeletionLeaseOwner = null;
        user.DeletionLeaseToken = null;
        user.DeletionAttemptCount = 0;
        user.DeletionNextAttemptAt = user.DeletionScheduledAt;
        user.DeletionLastError = null;
        user.DeletionDeadLetterAt = null;
        user.SecurityStamp = Guid.NewGuid().ToString();
        await FenceAttachmentWorkAsync(userId, user.DeletionEpoch, cancellationToken)
            .ConfigureAwait(false);

        if (actorUserId is { } actorId)
        {
            db.AdminAuditLogs.Add(new AdminAuditLog
            {
                AdminUserId = actorId,
                TargetUserId = userId,
                Action = "ScheduleUserDeletion",
                Reason = reason,
                Detail = $"scheduled={user.DeletionScheduledAt:O}",
                ClientIp = clientIp,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        var mutation = await _securityMutationCoordinator.ExecuteAsync(
                userId,
                SecurityEventType.AccountDeletionScheduled,
                $"scheduled={user.DeletionScheduledAt:O};reason={reason}",
                static _ => Task.CompletedTask,
                cancellationToken,
                securityEvent =>
                {
                    securityEvent.ClientIp = clientIp;
                    securityEvent.ActorUserId = actorUserId?.ToString();
                })
            .ConfigureAwait(false);
        if (!mutation.Succeeded)
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return AuthOperationResult.Fail("UpdateFailed", "用户安全版本无法推进");
        }
        await tx.CommitAsync(cancellationToken);

        try
        {
            await sessionStore.RevokeAllSessionsAsync(
                userId.ToString(), cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            // SecurityVersion is the durable authorization fence; Redis cleanup
            // is best-effort after commit and can safely be retried later.
            logger.LogWarning(ex, "预约注销后撤销会话失败 UserId={UserId}", userId);
        }

        logger.LogWarning("用户 {UserId} 已预约注销，冷静期至 {At}", userId, user.DeletionScheduledAt);
        return AuthOperationResult.Success();
    }

    public async Task<AuthOperationResult> CancelDeletionAsync(
        long userId, CancellationToken cancellationToken = default)
    {
        // 行锁：若 Worker 正在事务内清理会阻塞至其提交/回滚，避免半删后仍“取消成功”。
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {userId} FOR UPDATE""",
            cancellationToken);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return AuthOperationResult.Fail("NotFound", "用户不存在");
        }

        if (user.DeletionScheduledAt is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return AuthOperationResult.Fail("NotScheduled", "未预约注销");
        }

        user.DeletionScheduledAt = null;
        user.AccountState = AccountState.Active;
        user.DeletionEpoch = NextDeletionEpoch(user.DeletionEpoch);
        user.DeletionLeaseUntil = null;
        user.DeletionLeaseOwner = null;
        user.DeletionLeaseToken = null;
        user.DeletionAttemptCount = 0;
        user.DeletionNextAttemptAt = null;
        user.DeletionLastError = null;
        user.DeletionDeadLetterAt = null;
        user.SecurityStamp = Guid.NewGuid().ToString();
        await FenceAttachmentWorkAsync(userId, user.DeletionEpoch, cancellationToken)
            .ConfigureAwait(false);
        var mutation = await _securityMutationCoordinator.ExecuteAsync(
                userId,
                SecurityEventType.AccountDeletionCancelled,
                "account-deletion-cancelled",
                static _ => Task.CompletedTask,
                cancellationToken)
            .ConfigureAwait(false);
        if (!mutation.Succeeded)
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return AuthOperationResult.Fail("UpdateFailed", "用户安全版本无法推进");
        }
        await tx.CommitAsync(cancellationToken);
        return AuthOperationResult.Success();
    }

    public async Task<AccountDeletionStatusDto?> GetDeletionStatusAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new AccountDeletionStatusDto(
                user.AccountState == AccountState.Deleted
                    ? AccountState.Deleted
                    : user.DeletionScheduledAt != null && user.DeletionScheduledAt > now
                        ? AccountState.DeletionPending
                        : user.AccountState,
                user.DeletionScheduledAt,
                user.DeletionEpoch))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<UserDataExportDto?> ExportAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null) return null;

        var events = await db.SecurityEvents.AsNoTracking()
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.Id)
            .Take(500)
            .Select(e => new { e.Id, e.EventType, e.DeviceId, e.ClientIp, e.Detail, e.CreatedAt })
            .ToListAsync(cancellationToken);

        return new UserDataExportDto
        {
            UserId = user.Id,
            UserName = user.UserName,
            Email = user.Email,
            Signature = user.Signature,
            Region = user.Region,
            CreatedDate = user.CreatedDate,
            SecurityEvents = events.Cast<object>().ToList(),
        };
    }

    public async Task<int> ProcessDueDeletionsAsync(CancellationToken cancellationToken = default)
    {
        db.ChangeTracker.Clear();
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(DeletionLeaseDuration);
        var leaseOwner = CreateLeaseOwner();
        var leaseToken = CreateLeaseToken();

        List<long> claimedIds;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            claimedIds = await db.Database
                .SqlQuery<long>($"""
                    UPDATE "AspNetUsers" AS u
                    SET "DeletionLeaseUntil" = {leaseUntil},
                        "DeletionLeaseOwner" = {leaseOwner},
                        "DeletionLeaseToken" = {leaseToken}
                    WHERE u."Id" IN (
                        SELECT i."Id" FROM "AspNetUsers" AS i
                        WHERE i."DeletionScheduledAt" IS NOT NULL
                          AND i."DeletionScheduledAt" <= {now}
                          AND (i."DeletionLeaseUntil" IS NULL OR i."DeletionLeaseUntil" < {now})
                        ORDER BY i."DeletionScheduledAt"
                        FOR UPDATE SKIP LOCKED
                        LIMIT 1
                    )
                    RETURNING u."Id"
                    """)
                .ToListAsync(cancellationToken);

            await tx.CommitAsync(cancellationToken);
        }

        if (claimedIds.Count == 0) return 0;

        using var processingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = _scopeFactory is null
            ? null
            : RenewDeletionLeasesUntilStoppedAsync(
                claimedIds,
                leaseOwner,
                leaseToken,
                processingCts);
        var processed = 0;
        try
        {
            if (AfterClaimHook is not null)
                await AfterClaimHook(claimedIds, processingCts.Token);

            foreach (var userId in claimedIds)
            {
                try
                {
                    if (await TryPurgeUserAtomicallyAsync(
                            userId,
                            now,
                            processingCts.Token,
                            leaseOwner,
                            leaseToken))
                        processed++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // A confirmed lease loss cancels the external portion. Do
                    // not release a lease that may already belong to another
                    // worker; the fenced purge has already discarded it.
                    logger.LogInformation(
                        "注销用户 {UserId} 因租约丢失而取消，等待其他 Worker 接管",
                        userId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "注销用户 {UserId} 失败，释放租约以便重试", userId);
                    await ReleaseLeaseAsync(userId, leaseOwner, leaseToken, cancellationToken);
                }
            }
        }
        finally
        {
            processingCts.Cancel();
            if (heartbeat is not null)
            {
                try
                {
                    await heartbeat.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Normal worker shutdown.
                }
            }
        }

        if (processed > 0)
            logger.LogWarning("已执行 {Count} 个到期账号注销", processed);
        return processed;
    }

    /// <summary>
    /// LeasedJobExecutor 使用的按容量领取入口。每次调用为每个用户生成
    /// 独立 owner token，避免一个批次共享租约导致后段用户在执行前过期。
    /// </summary>
    public async Task<IReadOnlyList<AccountDeletionJob>> ClaimDueDeletionJobsAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        db.ChangeTracker.Clear();
        maxCount = Math.Clamp(maxCount, 1, 500);
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(DeletionLeaseDuration);

        if (db.Database.ProviderName?.Contains(
                "Npgsql", StringComparison.OrdinalIgnoreCase) != true)
        {
            var due = await db.Users
                .Where(user => user.DeletionScheduledAt != null
                               && user.DeletionScheduledAt <= now
                               && (user.DeletionNextAttemptAt == null
                                   || user.DeletionNextAttemptAt <= now)
                               && user.DeletionDeadLetterAt == null
                               && (user.DeletionLeaseUntil == null
                                   || user.DeletionLeaseUntil < now))
                .OrderBy(user => user.DeletionScheduledAt)
                .Take(maxCount)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var user in due)
            {
                user.DeletionLeaseOwner = CreateLeaseOwner();
                user.DeletionLeaseToken = CreateLeaseToken();
                user.DeletionLeaseUntil = leaseUntil;
                user.DeletionAttemptCount++;
            }

            if (due.Count > 0)
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return due.Select(user => new AccountDeletionJob
            {
                UserId = user.Id,
                ScheduledAt = user.DeletionScheduledAt!.Value,
                LeaseOwner = user.DeletionLeaseOwner!,
                LeaseToken = user.DeletionLeaseToken!,
                LeaseExpiresAt = leaseUntil,
                AttemptCount = user.DeletionAttemptCount,
            }).ToArray();
        }

        var leaseOwnerPrefix = CreateLeaseOwner();
        List<long> claimedIds;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            claimedIds = await db.Database
                .SqlQuery<long>($"""
                    UPDATE "AspNetUsers" AS u
                    SET "DeletionLeaseUntil" = {leaseUntil},
                        "DeletionLeaseOwner" = md5({leaseOwnerPrefix} || clock_timestamp()::text || u."Id"::text),
                        "DeletionLeaseToken" = md5(random()::text || clock_timestamp()::text || u."Id"::text),
                        "DeletionAttemptCount" = "DeletionAttemptCount" + 1
                    WHERE u."Id" IN (
                        SELECT i."Id" FROM "AspNetUsers" AS i
                        WHERE i."DeletionScheduledAt" IS NOT NULL
                          AND i."DeletionScheduledAt" <= {now}
                          AND (i."DeletionNextAttemptAt" IS NULL OR i."DeletionNextAttemptAt" <= {now})
                          AND i."DeletionDeadLetterAt" IS NULL
                          AND (i."DeletionLeaseUntil" IS NULL OR i."DeletionLeaseUntil" < {now})
                        ORDER BY i."DeletionScheduledAt"
                        FOR UPDATE SKIP LOCKED
                        LIMIT {maxCount}
                    )
                    RETURNING u."Id"
                    """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (claimedIds.Count == 0)
            return [];

        return await db.Users.AsNoTracking()
            .Where(user => claimedIds.Contains(user.Id)
                           && user.DeletionScheduledAt != null)
            .Select(user => new AccountDeletionJob
            {
                UserId = user.Id,
                ScheduledAt = user.DeletionScheduledAt!.Value,
                LeaseOwner = user.DeletionLeaseOwner!,
                LeaseToken = user.DeletionLeaseToken!,
                LeaseExpiresAt = user.DeletionLeaseUntil!.Value,
                AttemptCount = user.DeletionAttemptCount,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>按用户 owner token 续租；结果为 LeaseLost 才允许取消外部工作。</summary>
    public async Task<LeaseRenewalResult> RenewDeletionLeaseAsync(
        AccountDeletionJob job,
        CancellationToken cancellationToken = default)
    {
        if (job.UserId <= 0
            || string.IsNullOrWhiteSpace(job.LeaseOwner)
            || string.IsNullOrWhiteSpace(job.LeaseToken))
            return LeaseRenewalResult.LeaseLost;

        try
        {
            var until = DateTimeOffset.UtcNow.Add(DeletionLeaseDuration);
            var updated = await db.Users
                .Where(user => user.Id == job.UserId
                               && user.DeletionLeaseOwner == job.LeaseOwner
                               && user.DeletionLeaseToken == job.LeaseToken
                               && user.DeletionScheduledAt != null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        user => user.DeletionLeaseUntil,
                        until),
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
            logger.LogDebug(ex, "账号注销租约续租失败 UserId={UserId}", job.UserId);
            return LeaseRenewalResult.TransientFailure;
        }
    }

    public async Task<bool> ReleaseDeletionLeaseAsync(
        AccountDeletionJob job,
        string error,
        bool deadLetter,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var message = string.IsNullOrWhiteSpace(error)
            ? "account_deletion_failed"
            : error.Length <= 500 ? error : error[..500];
        var next = deadLetter
            ? (DateTimeOffset?)null
            : now.Add(LeasedJobBackoff.ExponentialWithJitter(
                TimeSpan.FromSeconds(5), Math.Max(1, job.AttemptCount), TimeSpan.FromHours(1)));

        return await db.Users
            .Where(user => user.Id == job.UserId
                           && user.DeletionLeaseOwner == job.LeaseOwner
                           && user.DeletionLeaseToken == job.LeaseToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(user => user.DeletionLeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(user => user.DeletionLeaseOwner, (string?)null)
                    .SetProperty(user => user.DeletionLeaseToken, (string?)null)
                    .SetProperty(user => user.DeletionNextAttemptAt, next)
                    .SetProperty(user => user.DeletionLastError, message)
                    .SetProperty(user => user.DeletionDeadLetterAt,
                        deadLetter ? now : (DateTimeOffset?)null),
                cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    private async Task RenewDeletionLeasesUntilStoppedAsync(
        IReadOnlyList<long> userIds,
        string leaseOwner,
        string leaseToken,
        CancellationTokenSource processingCts)
    {
        var interval = TimeSpan.FromTicks(
            Math.Max(TimeSpan.FromSeconds(1).Ticks, DeletionLeaseDuration.Ticks / 3));
        while (!processingCts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, processingCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (processingCts.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await using var scope = _scopeFactory!.CreateAsyncScope();
                var freshDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
                var until = DateTimeOffset.UtcNow.Add(DeletionLeaseDuration);
                var renewed = await freshDb.Users
                    .Where(user => userIds.Contains(user.Id)
                                   && user.DeletionLeaseOwner == leaseOwner
                                   && user.DeletionLeaseToken == leaseToken
                                   && user.DeletionScheduledAt != null)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            user => user.DeletionLeaseUntil,
                            until),
                        processingCts.Token)
                    .ConfigureAwait(false);
                if (renewed != userIds.Count)
                {
                    logger.LogWarning(
                        "账号注销租约已丢失，取消当前批次 UserIds={UserIds}",
                        string.Join(',', userIds));
                    processingCts.Cancel();
                    return;
                }
            }
            catch (OperationCanceledException) when (processingCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A transient renewal failure is ambiguous. Keep trying; the
                // final purge query still fences by owner and scheduled state.
                logger.LogDebug(ex, "账号注销租约续租暂时失败 UserIds={UserIds}", string.Join(',', userIds));
            }
        }
    }

    /// <summary>
    /// 在单事务内锁定用户、复核注销状态/租约，再物理删除关联数据并删除用户。
    /// 策略：用户自有数据物理删除；管理员审计日志匿名化保留；Realtime 消息通过 Outbox 事件异步清理。
    /// </summary>
    public async Task<bool> TryPurgeUserAtomicallyAsync(
        long userId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        string? expectedLeaseOwner = null,
        string? expectedLeaseToken = null)
    {
        db.ChangeTracker.Clear();
        var fencingOwner = expectedLeaseOwner ?? _instanceId;
        if (!attachmentMetadata.IsAvailable)
            throw new InvalidOperationException(
                $"附件元数据不可用，拒绝在无法建立删除墓碑时注销账户：{attachmentMetadata.UnavailableReason}");

        // External read happens before the local transaction. Failure is fail-closed;
        // once the snapshot succeeds, the local tombstones and user deletion commit
        // atomically below. A scheduled account cannot authenticate to create more uploads.
        var attachmentObjectKeys = (await attachmentMetadata
                .ListObjectKeysForUserAsync(userId, cancellationToken)
                .ConfigureAwait(false))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            await AdminRoleInvariant.AcquireMutationLockAsync(db, cancellationToken);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {userId} FOR UPDATE""",
                cancellationToken);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

            if (user is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            var scheduledAt = user.DeletionScheduledAt;
            var leaseOwner = user.DeletionLeaseOwner;
            var leaseToken = user.DeletionLeaseToken;

            // 取消注销或租约易主：整事务回滚，关联数据不会被部分删除。
            if (scheduledAt is null
                || scheduledAt > now
                || !string.Equals(leaseOwner, fencingOwner, StringComparison.Ordinal)
                || (expectedLeaseToken is not null
                    && !string.Equals(leaseToken, expectedLeaseToken, StringComparison.Ordinal)))
            {
                logger.LogInformation(
                    "跳过注销 UserId={UserId}：状态已变更（取消或租约不匹配）", userId);
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            if (await AdminRoleInvariant.IsLastActiveAdminAsync(db, userId, cancellationToken))
            {
                user.DeletionScheduledAt = null;
                user.DeletionEpoch = NextDeletionEpoch(user.DeletionEpoch);
                user.DeletionLeaseUntil = null;
                user.DeletionLeaseOwner = null;
                user.DeletionLeaseToken = null;
                user.SecurityStamp = Guid.NewGuid().ToString();
                await FenceAttachmentWorkAsync(userId, user.DeletionEpoch, cancellationToken)
                    .ConfigureAwait(false);
                var mutation = await _securityMutationCoordinator.ExecuteAsync(
                        userId,
                        SecurityEventType.AccountDeletionCancelled,
                        "account_deletion_cancelled_last_admin",
                        static _ => Task.CompletedTask,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!mutation.Succeeded)
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return false;
                }
                await tx.CommitAsync(cancellationToken);
                logger.LogWarning(
                    "已取消最后管理员的到期注销 UserId={UserId}", userId);
                return false;
            }

            // Advance every in-flight attachment workflow to the deletion
            // generation while the user row is locked. A worker that claimed
            // the previous generation can still finish its external call, but
            // its fenced local write/projection is no longer accepted.
            await FenceAttachmentWorkAsync(userId, user.DeletionEpoch, cancellationToken)
                .ConfigureAwait(false);

            var stagedAttachmentDeleteCount = 0;
            var avatarObjectKey = user.AvatarUrl;
            var objectKeysToDelete = attachmentObjectKeys
                .Append(avatarObjectKey)
                .OfType<string>()
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (objectKeysToDelete.Length > 0)
            {
                var existingKeys = await db.AttachmentBlobDeleteJobs
                    .AsNoTracking()
                    .Where(job => objectKeysToDelete.Contains(job.ObjectKey)
                                  && (job.Status == AttachmentBlobDeleteJobStatus.Pending
                                      || job.Status == AttachmentBlobDeleteJobStatus.AwaitingPublication
                                      || job.Status == AttachmentBlobDeleteJobStatus.Processing))
                    .Select(job => job.ObjectKey)
                    .ToListAsync(cancellationToken);
                var existing = existingKeys.ToHashSet(StringComparer.Ordinal);
                // Account deletion is authoritative: a final-avatar
                // publication candidate must become immediately deletable in
                // this transaction rather than waiting for its grace period.
                await db.AttachmentBlobDeleteJobs
                    .Where(job => objectKeysToDelete.Contains(job.ObjectKey)
                                  && job.Status == AttachmentBlobDeleteJobStatus.AwaitingPublication)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(
                                job => job.Status,
                                job => AttachmentBlobDeleteJobStatus.Pending)
                            .SetProperty(job => job.NextAttemptAt, now),
                        cancellationToken)
                    .ConfigureAwait(false);

                var tombstones = objectKeysToDelete
                    .Where(key => !existing.Contains(key))
                    .Select(key => new AttachmentBlobDeleteJob
                    {
                        ObjectKey = key,
                        UserId = userId,
                        StorageKind = string.Equals(key, avatarObjectKey, StringComparison.Ordinal)
                            ? AttachmentBlobDeleteStorageKind.Avatar
                            : AttachmentBlobDeleteStorageKind.Attachment,
                        Status = AttachmentBlobDeleteJobStatus.Pending,
                        AttemptCount = 0,
                        NextAttemptAt = now,
                        CreatedAt = now,
                    })
                    .ToArray();
                if (tombstones.Length > 0)
                {
                    db.AttachmentBlobDeleteJobs.AddRange(tombstones);
                    stagedAttachmentDeleteCount = tombstones.Length;
                }
            }

            // The tombstones above and user deletion now share this transaction.
            // Subsequent bulk deletes must not conflict with a tracked user entity.
            db.Entry(user).State = EntityState.Detached;

            await db.InAppNotifications
                .Where(n => n.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.NotificationOutbox
                .Where(n => n.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.SecurityEvents
                .Where(e => e.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.TrustedDevices
                .Where(d => d.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.UserReports
                .Where(r => r.ReporterId == userId || r.TargetUserId == userId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.UserRoles
                .Where(ur => ur.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);

            // 导出作业：事务内仅标 PendingDelete 墓碑，提交后再删 blob；成功后再删行。
            await db.DataExportJobs
                .Where(j => j.UserId == userId && j.ObjectKey != null)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(j => j.Status, Core.Models.Export.DataExportJobStatus.PendingDelete)
                        .SetProperty(j => j.ConsumedAt, now)
                        .SetProperty(j => j.LeaseOwner, (string?)null)
                        .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                        .SetProperty(j => j.Error, "account_deletion_pending_blob_delete"),
                    cancellationToken);
            await db.DataExportJobs
                .Where(j => j.UserId == userId && j.ObjectKey == null)
                .ExecuteDeleteAsync(cancellationToken);

            // 审计日志：匿名化保留（不物理删除），满足合规追溯。
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "T_AdminAuditLog"
                SET "TargetUserId" = CASE WHEN "TargetUserId" = {userId} THEN NULL ELSE "TargetUserId" END,
                    "Detail" = COALESCE("Detail", '') || {$" [anonymized-user:{userId}]"},
                    "AdminUserId" = CASE WHEN "AdminUserId" = {userId} THEN 0 ELSE "AdminUserId" END
                WHERE "TargetUserId" = {userId} OR "AdminUserId" = {userId}
                """, cancellationToken);

            // Realtime 消息/会话清理：可靠 Outbox 事件（Saga 由 Realtime 侧消费）。
            var cleanupEventId = Guid.NewGuid().ToString("N");
            var evt = new RealtimeEvent
            {
                EventId = cleanupEventId,
                Type = RealtimeEventType.UserAccountDeleted,
                TargetUserId = userId,
                ActorUserId = userId,
                OccurredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PayloadJson = RealtimeWireSerializer.Serialize(new RealtimeDomainNotificationPayload
                {
                    Resource = "user-account",
                    Action = "deleted",
                    ResourceId = userId.ToString(),
                    Message = "account-deleted",
                }),
            };
            db.RealtimeOutbox.Add(RealtimeIntegrationOutboxItem.FromEvent(evt));
            var sagaNow = DateTimeOffset.UtcNow;
            db.AccountCleanupSagas.Add(new Core.Models.Export.AccountCleanupSaga
            {
                UserId = userId,
                EventId = cleanupEventId,
                Status = Core.Models.Export.AccountCleanupSagaStatus.Pending,
                CreatedAt = sagaNow,
                UpdatedAt = sagaNow,
            });

            var deleted = await db.Users
                    .Where(u => u.Id == userId
                            && u.DeletionScheduledAt != null
                            && u.DeletionScheduledAt <= now
                            && u.DeletionLeaseOwner == fencingOwner
                            && (expectedLeaseToken == null
                                || u.DeletionLeaseToken == expectedLeaseToken))
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            // Redis / 导出 blob / 附件 blob 在 DB 事务提交后再撤销，避免持行锁等待外部 IO。
            try
            {
                await sessionStore.RevokeAllSessionsAsync(userId.ToString(), cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "注销后撤销会话失败 UserId={UserId}（DB 已清理）", userId);
            }

            if (stagedAttachmentDeleteCount > 0)
            {
                AuthSecurityMetrics.AttachmentPendingDeleteDelta(stagedAttachmentDeleteCount);
                logger.LogInformation(
                    "账户注销事务已持久化 {Count} 条附件删除墓碑 UserId={UserId}",
                    stagedAttachmentDeleteCount,
                    userId);
            }

            // Tombstones are already durable. Immediate processing is an
            // optimization only; failure leaves Pending rows for the worker.
            try
            {
                if (stagedAttachmentDeleteCount > 0)
                    await attachmentBlobDeletes.ProcessDueAsync(cancellationToken).ConfigureAwait(false);
                await attachmentMetadata.MarkAbandonedByUploaderAsync(userId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "注销后附件 blob GC 处理失败 UserId={UserId}", userId);
            }

            var pendingExports = await db.DataExportJobs
                .Where(j => j.UserId == userId && j.Status == Core.Models.Export.DataExportJobStatus.PendingDelete)
                .ToListAsync(cancellationToken);
            foreach (var job in pendingExports)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(job.ObjectKey))
                        await dataExportBlobs.DeleteAsync(job.ObjectKey, cancellationToken);
                    AuthSecurityMetrics.ExportBlobDelete("success");
                    db.DataExportJobs.Remove(job);
                }
                catch (Exception ex)
                {
                    AuthSecurityMetrics.ExportBlobDelete("failed");
                    AuthSecurityMetrics.ExportPendingDeleteDelta(1);
                    job.AttemptCount = Math.Max(1, job.AttemptCount + 1);
                    job.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                    logger.LogWarning(
                        ex,
                        "注销后删除导出对象失败，保留 PendingDelete 墓碑 UserId={UserId} Key={Key}",
                        userId,
                        job.ObjectKey);
                }
            }

            if (pendingExports.Count > 0)
                await db.SaveChangesAsync(cancellationToken);

            return true;
        });
    }

    private Task ReleaseLeaseAsync(
        long userId,
        string leaseOwner,
        string? leaseToken,
        CancellationToken cancellationToken)
        => db.Users
            .Where(u => u.Id == userId
                        && u.DeletionLeaseOwner == leaseOwner
                        && (leaseToken == null || u.DeletionLeaseToken == leaseToken))
            .ExecuteUpdateAsync(
                s => s.SetProperty(u => u.DeletionLeaseUntil, (DateTimeOffset?)null)
                     .SetProperty(u => u.DeletionLeaseOwner, (string?)null)
                     .SetProperty(u => u.DeletionLeaseToken, (string?)null),
                cancellationToken);

    private async Task FenceAttachmentWorkAsync(
        long userId,
        long deletionEpoch,
        CancellationToken cancellationToken)
    {
        await db.AttachmentScanJobs
            .Where(job => job.UserId == userId
                          && (job.Status == AttachmentScanJobStatus.Pending
                              || job.Status == AttachmentScanJobStatus.Processing
                              || job.Status == AttachmentScanJobStatus.Finalizing))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    job => job.UploaderDeletionEpoch,
                    deletionEpoch),
                cancellationToken)
            .ConfigureAwait(false);

        await db.AttachmentScanProjections
            .Where(projection => projection.UserId == userId
                                 && (projection.Status == AttachmentScanProjectionStatus.Pending
                                     || projection.Status == AttachmentScanProjectionStatus.Processing))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    projection => projection.UploaderDeletionEpoch,
                    deletionEpoch),
                cancellationToken)
            .ConfigureAwait(false);

        await db.AttachmentConfirmSagas
            .Where(saga => saga.UserId == userId
                           && saga.Status != AttachmentConfirmSagaStatus.Completed
                           && saga.Status != AttachmentConfirmSagaStatus.Failed)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    saga => saga.UploaderDeletionEpoch,
                    deletionEpoch),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static long NextDeletionEpoch(long current)
    {
        if (current == long.MaxValue)
            throw new InvalidOperationException("DeletionEpoch 已达到最大值，无法继续推进");
        return current + 1;
    }

    private string CreateLeaseOwner()
    {
        var prefix = _instanceId.Length > 32 ? _instanceId[..32] : _instanceId;
        var value = $"{prefix}:{Guid.NewGuid():N}";
        return value.Length <= 64 ? value : value[..64];
    }

    private static string CreateLeaseToken() => Guid.NewGuid().ToString("N");
}

public sealed class AccountDeletionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerConcurrencyOptions> workerConcurrencyOptions,
    ILeasedJobStore<AccountDeletionJob> jobStore,
    LeasedJobExecutor<AccountDeletionJob> executor,
    ILogger<AccountDeletionWorker> logger) : BackgroundService
{
    private const string WorkerName = "account_deletion";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerConcurrency = Math.Max(1, workerConcurrencyOptions.Value.AccountDeletion);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var completed = await executor.DrainAsync(
                        WorkerName,
                        workerConcurrency,
                        AccountLifecycleService.DeletionLeaseDuration,
                        jobStore,
                        ExecuteClaimedAsync,
                        job => job.AttemptCount >= Math.Max(
                            1, workerConcurrencyOptions.Value.AccountDeletionMaxAttempts),
                        stoppingToken)
                    .ConfigureAwait(false);

                // Drain keeps claiming until the queue is empty. A short
                // delay prevents a failed/released row from hot-spinning;
                // the next drain will still reclaim it promptly.
                await Task.Delay(
                        completed > 0
                            ? TimeSpan.FromMilliseconds(100)
                            : TimeSpan.FromSeconds(5),
                        stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "账号注销后台任务失败");
            }
        }
    }

    private async Task ExecuteClaimedAsync(
        AccountDeletionJob job,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountLifecycleService>();
        job.Terminal = await service.TryPurgeUserAtomicallyAsync(
                job.UserId,
                DateTimeOffset.UtcNow,
                cancellationToken,
                job.LeaseOwner,
                job.LeaseToken)
            .ConfigureAwait(false);
    }
}

public sealed class NotificationQuery(UserDbContext db) : INotificationQuery
{
    public async Task<CursorPage<InAppNotificationDto>> ListAsync(
        long userId, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(limit, 1, 100);
        long? cursorId = long.TryParse(cursor, out var c) ? c : null;
        var query = db.InAppNotifications.AsNoTracking().Where(n => n.UserId == userId);
        if (cursorId.HasValue) query = query.Where(n => n.Id < cursorId.Value);

        var rows = await query.OrderByDescending(n => n.Id).Take(pageSize + 1)
            .Select(n => new InAppNotificationDto
            {
                Id = n.Id,
                Type = n.Type,
                Title = n.Title,
                Body = n.Body,
                IsRead = n.IsRead,
                CreatedAt = n.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new CursorPage<InAppNotificationDto>
        {
            Items = rows,
            HasMore = hasMore,
            NextCursor = hasMore && rows.Count > 0 ? rows[^1].Id.ToString() : null,
        };
    }

    public async Task MarkReadAsync(long userId, long notificationId, CancellationToken cancellationToken = default)
    {
        await db.InAppNotifications
            .Where(n => n.Id == notificationId && n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(
                s => s.SetProperty(n => n.IsRead, true),
                cancellationToken);
    }

    public Task<int> CountUnreadAsync(long userId, CancellationToken cancellationToken = default)
        => db.InAppNotifications.AsNoTracking()
            .CountAsync(n => n.UserId == userId && !n.IsRead, cancellationToken);

    public async Task<int> MarkReadBatchAsync(
        long userId, IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return 0;
        var distinct = ids.Distinct().Take(100).ToArray();
        return await db.InAppNotifications
            .Where(n => n.UserId == userId && distinct.Contains(n.Id) && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), cancellationToken);
    }
}
