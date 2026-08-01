using ChatApp.Realtime.Abstractions.Events;
using ChatApp.Realtime.Integration.Outbox;
using ChatApp.Realtime.Integration.Serialization;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models.Auth;
using Core.Models.Common;
using Core.Models.Export;
using Core.Models.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class AccountLifecycleService(
    UserDbContext db,
    ISessionStore sessionStore,
    ISecurityEventStore securityEventStore,
    IDataExportBlobStore dataExportBlobs,
    IAttachmentMetadataStore attachmentMetadata,
    IAttachmentBlobDeleteService attachmentBlobDeletes,
    ILogger<AccountLifecycleService> logger) : IAccountLifecycleService
{
    public static readonly TimeSpan CoolDown = TimeSpan.FromDays(14);
    private readonly string _instanceId = Environment.MachineName + ":" + Guid.NewGuid().ToString("N")[..8];
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

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
        user.DeletionLeaseUntil = null;
        user.DeletionLeaseOwner = null;
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.AdvanceSecurityVersion();

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

        await securityEventStore.RecordAsync(
            userId,
            SecurityEventType.AccountDeletionScheduled,
            clientIp: clientIp,
            detail: $"scheduled={user.DeletionScheduledAt:O};reason={reason}",
            actorUserId: actorUserId?.ToString(),
            cancellationToken: cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
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
        user.DeletionLeaseUntil = null;
        user.DeletionLeaseOwner = null;
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return AuthOperationResult.Success();
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

        var friendIds = await db.Friendships.AsNoTracking()
            .Where(f => f.UserId == userId)
            .Select(f => f.FriendId)
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
            FriendIds = friendIds.Cast<object>().ToList(),
        };
    }

    public async Task<int> ProcessDueDeletionsAsync(CancellationToken cancellationToken = default)
    {
        db.ChangeTracker.Clear();
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(LeaseDuration);

        List<long> claimedIds;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            claimedIds = await db.Database
                .SqlQuery<long>($"""
                    UPDATE "AspNetUsers" AS u
                    SET "DeletionLeaseUntil" = {leaseUntil},
                        "DeletionLeaseOwner" = {_instanceId}
                    WHERE u."Id" IN (
                        SELECT i."Id" FROM "AspNetUsers" AS i
                        WHERE i."DeletionScheduledAt" IS NOT NULL
                          AND i."DeletionScheduledAt" <= {now}
                          AND (i."DeletionLeaseUntil" IS NULL OR i."DeletionLeaseUntil" < {now})
                        ORDER BY i."DeletionScheduledAt"
                        FOR UPDATE SKIP LOCKED
                        LIMIT 50
                    )
                    RETURNING u."Id"
                    """)
                .ToListAsync(cancellationToken);

            await tx.CommitAsync(cancellationToken);
        }

        if (claimedIds.Count == 0) return 0;

        if (AfterClaimHook is not null)
            await AfterClaimHook(claimedIds, cancellationToken);

        var processed = 0;
        foreach (var userId in claimedIds)
        {
            try
            {
                if (await TryPurgeUserAtomicallyAsync(userId, now, cancellationToken))
                    processed++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "注销用户 {UserId} 失败，释放租约以便重试", userId);
                await ReleaseLeaseAsync(userId, cancellationToken);
            }
        }

        if (processed > 0)
            logger.LogWarning("已执行 {Count} 个到期账号注销", processed);
        return processed;
    }

    /// <summary>
    /// 在单事务内锁定用户、复核注销状态/租约，再物理删除关联数据并删除用户。
    /// 策略：用户自有数据物理删除；管理员审计日志匿名化保留；Realtime 消息通过 Outbox 事件异步清理。
    /// </summary>
    public async Task<bool> TryPurgeUserAtomicallyAsync(
        long userId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        db.ChangeTracker.Clear();
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

            // 取消注销或租约易主：整事务回滚，关联数据不会被部分删除。
            if (scheduledAt is null
                || scheduledAt > now
                || !string.Equals(leaseOwner, _instanceId, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "跳过注销 UserId={UserId}：状态已变更（取消或租约不匹配）", userId);
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            if (await AdminRoleInvariant.IsLastActiveAdminAsync(db, userId, cancellationToken))
            {
                user.DeletionScheduledAt = null;
                user.DeletionLeaseUntil = null;
                user.DeletionLeaseOwner = null;
                db.SecurityEvents.Add(new SecurityEvent
                {
                    UserId = userId,
                    EventType = SecurityEventType.AdminAction,
                    Detail = "account_deletion_cancelled_last_admin",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                logger.LogWarning(
                    "已取消最后管理员的到期注销 UserId={UserId}", userId);
                return false;
            }

            var stagedAttachmentDeleteCount = 0;
            if (attachmentObjectKeys.Length > 0)
            {
                var existingKeys = await db.AttachmentBlobDeleteJobs
                    .AsNoTracking()
                    .Where(job => attachmentObjectKeys.Contains(job.ObjectKey)
                                  && (job.Status == AttachmentBlobDeleteJobStatus.Pending
                                      || job.Status == AttachmentBlobDeleteJobStatus.Processing))
                    .Select(job => job.ObjectKey)
                    .ToListAsync(cancellationToken);
                var existing = existingKeys.ToHashSet(StringComparer.Ordinal);
                var tombstones = attachmentObjectKeys
                    .Where(key => !existing.Contains(key))
                    .Select(key => new AttachmentBlobDeleteJob
                    {
                        ObjectKey = key,
                        UserId = userId,
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

            await db.Friendships
                .Where(f => f.UserId == userId || f.FriendId == userId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.FriendRequests
                .Where(r => r.RequesterId == userId || r.TargetUserId == userId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.BlockRecords
                .Where(b => b.BlockerId == userId || b.BlockedUserId == userId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.FriendGroups
                .Where(g => g.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);
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
                            && u.DeletionLeaseOwner == _instanceId)
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

    private Task ReleaseLeaseAsync(long userId, CancellationToken cancellationToken)
        => db.Users
            .Where(u => u.Id == userId && u.DeletionLeaseOwner == _instanceId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(u => u.DeletionLeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(u => u.DeletionLeaseOwner, (string?)null),
                cancellationToken);
}

public sealed class AccountDeletionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<AccountDeletionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var svc = scope.ServiceProvider.GetRequiredService<IAccountLifecycleService>();
                await svc.ProcessDueDeletionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "账号注销后台任务失败");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
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
