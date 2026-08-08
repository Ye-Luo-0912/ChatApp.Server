using ChatApp.Realtime.Abstractions.Events;
using ChatApp.Realtime.Abstractions.Stores;
using ChatApp.Realtime.Integration.Outbox;
using ChatApp.Realtime.Integration.Serialization;
using Core.Interfaces;
using Core.Models.Export;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class AccountCleanupSagaService(
    UserDbContext db,
    ILogger<AccountCleanupSagaService> logger) : IAccountCleanupSagaService
{
    public const string CompletedEventIdPrefix = "cleanup-done:";

    public async Task<AccountCleanupApplyResult> TryCompleteAsync(
        long userId,
        string completedEventId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(completedEventId))
            return AccountCleanupApplyResult.InvalidCompletedEventId;

        var existingInbox = await db.AccountCleanupInbox
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.EventId == completedEventId, cancellationToken);
        if (existingInbox is not null)
            return AccountCleanupApplyResult.DuplicateDelivery;

        var sourceEventId = TryGetSourceEventId(completedEventId);
        if (sourceEventId is null)
        {
            logger.LogWarning(
                "AccountCleanupCompleted EventId 缺少 cleanup-done: 前缀。UserId={UserId}；EventId={EventId}",
                userId,
                completedEventId);
            return AccountCleanupApplyResult.InvalidCompletedEventId;
        }

        var saga = await db.AccountCleanupSagas
            .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
        if (saga is null)
        {
            logger.LogWarning(
                "收到 AccountCleanupCompleted 但无 Saga。UserId={UserId}；EventId={EventId}",
                userId,
                completedEventId);
            return AccountCleanupApplyResult.MissingSaga;
        }

        if (saga.Status == AccountCleanupSagaStatus.Completed)
        {
            await EnsureInboxAsync(
                completedEventId,
                userId,
                AccountCleanupInboxOutcome.Completed,
                cancellationToken);
            return AccountCleanupApplyResult.AlreadyCompleted;
        }

        if (!string.Equals(sourceEventId, saga.EventId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "AccountCleanupCompleted EventId 与 Saga 不一致，拒绝完成。UserId={UserId}；SagaEventId={SagaEventId}；CompletedEventId={CompletedEventId}",
                userId,
                saga.EventId,
                completedEventId);
            return AccountCleanupApplyResult.EventIdMismatch;
        }

        var now = DateTimeOffset.UtcNow;
        saga.Status = AccountCleanupSagaStatus.Completed;
        saga.CompletedAt = now;
        saga.UpdatedAt = now;
        saga.LastError = null;
        await EnsureInboxAsync(
            completedEventId,
            userId,
            AccountCleanupInboxOutcome.Completed,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        AuthSecurityMetrics.RecordAccountCleanup("completed");
        logger.LogInformation(
            "AccountCleanupSaga 已完成。UserId={UserId}；EventId={EventId}",
            userId,
            saga.EventId);
        return AccountCleanupApplyResult.Completed;
    }

    public Task<AccountCleanupApplyResult> TryApplyCompletedEventAsync(
        RealtimeEvent evt,
        CancellationToken cancellationToken = default)
    {
        if (evt.Type != RealtimeEventType.AccountCleanupCompleted)
            return Task.FromResult(AccountCleanupApplyResult.InvalidCompletedEventId);
        return TryCompleteAsync(evt.TargetUserId, evt.EventId, cancellationToken);
    }

    public async Task<int> FailStalePendingAsync(
        TimeSpan maxAge,
        CancellationToken cancellationToken = default)
    {
        if (maxAge <= TimeSpan.Zero)
            return 0;

        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var now = DateTimeOffset.UtcNow;
        const string error = "pending_timeout";
        var stale = await db.AccountCleanupSagas
            .Where(s => s.Status == AccountCleanupSagaStatus.Pending && s.CreatedAt < cutoff)
            .ToListAsync(cancellationToken);
        if (stale.Count == 0)
            return 0;

        foreach (var saga in stale)
        {
            saga.Status = AccountCleanupSagaStatus.Failed;
            saga.CompletedAt = now;
            saga.UpdatedAt = now;
            saga.LastError = error;
        }

        await db.SaveChangesAsync(cancellationToken);
        AuthSecurityMetrics.RecordAccountCleanup("stale_failed", stale.Count);
        logger.LogWarning(
            "AccountCleanupSaga 超时失败 {Count} 条（CreatedAt < {Cutoff:o}）",
            stale.Count,
            cutoff);
        return stale.Count;
    }

    public Task RecordDeadLetterAsync(
        string eventId,
        long userId,
        string? payloadJson,
        string reasonCode,
        string reason,
        ulong? deliveryCount,
        CancellationToken cancellationToken = default)
        => RecordDeadLetterCoreAsync(eventId, userId, payloadJson, reasonCode, reason, deliveryCount, cancellationToken);

    /// <summary>Worker 便捷入口：从 RealtimeEvent 写 DLQ。</summary>
    public Task RecordDeadLetterAsync(
        RealtimeEvent evt,
        string reasonCode,
        string reason,
        ulong? deliveryCount,
        CancellationToken cancellationToken = default)
        => RecordDeadLetterCoreAsync(
            evt.EventId,
            evt.TargetUserId,
            evt.PayloadJson,
            reasonCode,
            reason,
            deliveryCount,
            cancellationToken);

    private async Task RecordDeadLetterCoreAsync(
        string? eventId,
        long userId,
        string? payloadJson,
        string reasonCode,
        string reason,
        ulong? deliveryCount,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            eventId = $"unknown-{Guid.NewGuid():N}";

        var inboxOutcome = reasonCode switch
        {
            AccountCleanupDeadLetterReason.EventIdMismatch => AccountCleanupInboxOutcome.DeadLetterMismatch,
            AccountCleanupDeadLetterReason.InvalidCompletedEventId => AccountCleanupInboxOutcome.DeadLetterInvalid,
            _ => AccountCleanupInboxOutcome.DeadLetterMissingExhausted,
        };

        var already = await db.AccountCleanupDeadLetters
            .AnyAsync(x => x.EventId == eventId && x.ReasonCode == reasonCode, cancellationToken);
        if (!already)
        {
            db.AccountCleanupDeadLetters.Add(new AccountCleanupDeadLetter
            {
                EventId = eventId,
                UserId = userId,
                ReasonCode = reasonCode,
                Reason = Truncate(reason, 500),
                PayloadJson = Truncate(payloadJson, 4000),
                DeliveryCount = deliveryCount is null ? null : (long)deliveryCount.Value,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await EnsureInboxAsync(eventId, userId, inboxOutcome, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        AuthSecurityMetrics.RecordAccountCleanup($"dlq_{reasonCode}");
        logger.LogError(
            "AccountCleanup 完成事件进入 DLQ。UserId={UserId}；EventId={EventId}；Reason={ReasonCode}；Delivery={DeliveryCount}",
            userId,
            eventId,
            reasonCode,
            deliveryCount);
    }

    public async Task<AccountCleanupReplayResponse> TryReplayAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            return new AccountCleanupReplayResponse
            {
                Outcome = AccountCleanupReplayOutcome.InvalidUser,
                Message = "无效的 UserId",
            };
        }

        var saga = await db.AccountCleanupSagas
            .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
        if (saga is null)
        {
            return new AccountCleanupReplayResponse
            {
                Outcome = AccountCleanupReplayOutcome.NotFound,
                Message = "未找到账号清理 Saga",
            };
        }

        if (saga.Status == AccountCleanupSagaStatus.Completed)
        {
            return new AccountCleanupReplayResponse
            {
                Outcome = AccountCleanupReplayOutcome.AlreadyCompleted,
                Message = "Saga 已 Completed，拒绝不安全重放",
                Item = await BuildItemDtoAsync(saga, cancellationToken),
            };
        }

        if (saga.Status is not (AccountCleanupSagaStatus.Failed or AccountCleanupSagaStatus.Pending))
        {
            return new AccountCleanupReplayResponse
            {
                Outcome = AccountCleanupReplayOutcome.NotFound,
                Message = $"不支持的 Saga 状态：{saga.Status}",
                Item = await BuildItemDtoAsync(saga, cancellationToken),
            };
        }

        var completedEventId = CompletedEventIdPrefix + saga.EventId;
        var staleInbox = await db.AccountCleanupInbox
            .Where(x => x.EventId == completedEventId
                        && x.Outcome != AccountCleanupInboxOutcome.Completed)
            .ToListAsync(cancellationToken);
        if (staleInbox.Count > 0)
            db.AccountCleanupInbox.RemoveRange(staleInbox);

        var now = DateTimeOffset.UtcNow;
        saga.Status = AccountCleanupSagaStatus.Pending;
        saga.CompletedAt = null;
        saga.LastError = null;
        saga.UpdatedAt = now;
        saga.ReplayCount += 1;

        var nowMs = now.ToUnixTimeMilliseconds();
        var existingOutbox = await db.RealtimeOutbox
            .FirstOrDefaultAsync(o => o.EventId == saga.EventId, cancellationToken);
        if (existingOutbox is not null)
            db.RealtimeOutbox.Remove(existingOutbox);

        var evt = new RealtimeEvent
        {
            EventId = saga.EventId,
            Type = RealtimeEventType.UserAccountDeleted,
            TargetUserId = userId,
            ActorUserId = userId,
            OccurredAtMs = nowMs,
            PayloadJson = RealtimeWireSerializer.Serialize(new RealtimeDomainNotificationPayload
            {
                Resource = "user-account",
                Action = "deleted",
                ResourceId = userId.ToString(),
                Message = "account-deleted-replay",
            }),
        };
        db.RealtimeOutbox.Add(RealtimeIntegrationOutboxItem.FromEvent(evt));
        await db.SaveChangesAsync(cancellationToken);

        AuthSecurityMetrics.RecordAccountCleanup("replay");
        logger.LogWarning(
            "AccountCleanupSaga 人工重放。UserId={UserId}；EventId={EventId}；ReplayCount={ReplayCount}",
            userId,
            saga.EventId,
            saga.ReplayCount);

        return new AccountCleanupReplayResponse
        {
            Outcome = AccountCleanupReplayOutcome.Replayed,
            Message = "已重新投递 UserAccountDeleted",
            Item = await BuildItemDtoAsync(saga, cancellationToken),
        };
    }

    public async Task<AccountCleanupReconcileResponse> TryReconcileAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            return new AccountCleanupReconcileResponse
            {
                Outcome = AccountCleanupReconcileOutcome.InvalidUser,
                Message = "无效的 UserId",
            };
        }

        var saga = await db.AccountCleanupSagas
            .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
        if (saga is null)
        {
            return new AccountCleanupReconcileResponse
            {
                Outcome = AccountCleanupReconcileOutcome.NotFound,
                Message = "未找到账号清理 Saga",
            };
        }

        if (saga.Status == AccountCleanupSagaStatus.Completed)
        {
            return new AccountCleanupReconcileResponse
            {
                Outcome = AccountCleanupReconcileOutcome.AlreadyCompleted,
                Message = "Saga 已 Completed",
                Item = await BuildItemDtoAsync(saga, cancellationToken),
            };
        }

        var completedEventId = CompletedEventIdPrefix + saga.EventId;
        var inboxCompleted = await db.AccountCleanupInbox
            .AsNoTracking()
            .AnyAsync(
                x => x.EventId == completedEventId
                     && x.Outcome == AccountCleanupInboxOutcome.Completed,
                cancellationToken);

        if (inboxCompleted)
        {
            var now = DateTimeOffset.UtcNow;
            saga.Status = AccountCleanupSagaStatus.Completed;
            saga.CompletedAt = now;
            saga.UpdatedAt = now;
            saga.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
            AuthSecurityMetrics.RecordAccountCleanup("reconcile_completed");
            logger.LogWarning(
                "AccountCleanupSaga 对账：Inbox Completed 证据推进完成。UserId={UserId}；EventId={EventId}",
                userId,
                saga.EventId);

            return new AccountCleanupReconcileResponse
            {
                Outcome = AccountCleanupReconcileOutcome.MarkedCompletedFromInbox,
                Message = "已根据 Inbox Completed 证据标为 Completed",
                Item = await BuildItemDtoAsync(saga, cancellationToken),
            };
        }

        var outbox = await db.RealtimeOutbox
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.EventId == saga.EventId, cancellationToken);
        if (saga.Status == AccountCleanupSagaStatus.Pending
            && outbox is not null
            && outbox.Status == (short)RealtimeOutboxStatus.Dead)
        {
            var now = DateTimeOffset.UtcNow;
            saga.Status = AccountCleanupSagaStatus.Failed;
            saga.CompletedAt = now;
            saga.UpdatedAt = now;
            saga.LastError = "outbox_dead";
            await db.SaveChangesAsync(cancellationToken);
            AuthSecurityMetrics.RecordAccountCleanup("reconcile_outbox_dead");
            logger.LogWarning(
                "AccountCleanupSaga 对账：Outbox Dead，标 Failed。UserId={UserId}；EventId={EventId}",
                userId,
                saga.EventId);

            return new AccountCleanupReconcileResponse
            {
                Outcome = AccountCleanupReconcileOutcome.MarkedFailedFromOutboxDead,
                Message = "UserAccountDeleted Outbox 已 Dead，Saga 已标 Failed（可再重放）",
                Item = await BuildItemDtoAsync(saga, cancellationToken),
            };
        }

        return new AccountCleanupReconcileResponse
        {
            Outcome = AccountCleanupReconcileOutcome.NoEvidence,
            Message = "无 Inbox Completed / Outbox Dead 证据，可人工重放或等待完成事件",
            Item = await BuildItemDtoAsync(saga, cancellationToken),
        };
    }

    public async Task<AccountCleanupSagaItemDto?> GetStatusAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
            return null;

        var saga = await db.AccountCleanupSagas
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
        if (saga is null)
            return null;

        return await BuildItemDtoAsync(saga, cancellationToken);
    }

    public async Task<AccountCleanupSagaListResponse> ListAsync(
        string? status,
        long? userId,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 200);

        var normalized = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        IQueryable<AccountCleanupSaga> query = db.AccountCleanupSagas.AsNoTracking();

        if (userId is > 0)
            query = query.Where(s => s.UserId == userId.Value);

        if (string.Equals(normalized, AccountCleanupDisplayStatus.DeadLetter, StringComparison.OrdinalIgnoreCase))
        {
            var deadUserIds = db.AccountCleanupDeadLetters.AsNoTracking()
                .Select(d => d.UserId)
                .Distinct();
            query = query.Where(s =>
                s.Status != AccountCleanupSagaStatus.Completed
                && deadUserIds.Contains(s.UserId));
        }
        else if (normalized is not null)
        {
            query = query.Where(s => s.Status == normalized);
        }

        var total = await query.CountAsync(cancellationToken);
        var sagas = await query
            .OrderByDescending(s => s.UpdatedAt)
            .ThenByDescending(s => s.UserId)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var items = await BuildItemDtosAsync(sagas, cancellationToken);

        return new AccountCleanupSagaListResponse
        {
            Items = items,
            Total = total,
            Offset = offset,
            Limit = limit,
        };
    }

    public async Task<IReadOnlyList<AccountCleanupDeadLetterDto>> ListDeadLettersAsync(
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 200);

        return await db.AccountCleanupDeadLetters
            .AsNoTracking()
            .OrderByDescending(x => x.Id)
            .Skip(offset)
            .Take(limit)
            .Select(x => new AccountCleanupDeadLetterDto(
                x.Id,
                x.EventId,
                x.UserId,
                x.ReasonCode,
                x.Reason,
                x.DeliveryCount,
                x.CreatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AccountCleanupDeadLetterDto?> GetDeadLetterAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        if (id <= 0)
            return null;

        return await db.AccountCleanupDeadLetters
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new AccountCleanupDeadLetterDto(
                x.Id,
                x.EventId,
                x.UserId,
                x.ReasonCode,
                x.Reason,
                x.DeliveryCount,
                x.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public static string? TryGetSourceEventId(string completedEventId)
    {
        if (completedEventId.StartsWith(CompletedEventIdPrefix, StringComparison.Ordinal))
            return completedEventId[CompletedEventIdPrefix.Length..];
        return null;
    }

    private async Task<AccountCleanupSagaItemDto> BuildItemDtoAsync(
        AccountCleanupSaga saga,
        CancellationToken cancellationToken)
    {
        var items = await BuildItemDtosAsync([saga], cancellationToken);
        return items[0];
    }

    private async Task<IReadOnlyList<AccountCleanupSagaItemDto>> BuildItemDtosAsync(
        IReadOnlyList<AccountCleanupSaga> sagas,
        CancellationToken cancellationToken)
    {
        if (sagas.Count == 0)
            return [];

        var userIds = sagas.Select(s => s.UserId).Distinct().ToList();
        var eventIds = sagas.Select(s => s.EventId).Distinct().ToList();
        var completedEventIds = eventIds.Select(id => CompletedEventIdPrefix + id).ToList();

        var completedInboxIds = await db.AccountCleanupInbox
            .AsNoTracking()
            .Where(x => completedEventIds.Contains(x.EventId)
                        && x.Outcome == AccountCleanupInboxOutcome.Completed)
            .Select(x => x.EventId)
            .ToListAsync(cancellationToken);
        var completedSet = completedInboxIds.ToHashSet(StringComparer.Ordinal);

        var deadLetters = await db.AccountCleanupDeadLetters
            .AsNoTracking()
            .Where(d => userIds.Contains(d.UserId))
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .ToListAsync(cancellationToken);
        var latestDlqByUser = deadLetters
            .GroupBy(d => d.UserId)
            .ToDictionary(g => g.Key, g => g.First());

        var outboxes = await db.RealtimeOutbox
            .AsNoTracking()
            .Where(o => eventIds.Contains(o.EventId))
            .ToListAsync(cancellationToken);
        var outboxByEvent = outboxes.ToDictionary(o => o.EventId, StringComparer.Ordinal);

        var result = new List<AccountCleanupSagaItemDto>(sagas.Count);
        foreach (var saga in sagas)
        {
            latestDlqByUser.TryGetValue(saga.UserId, out var latestDlq);
            outboxByEvent.TryGetValue(saga.EventId, out var outbox);
            var hasCompletedInbox = completedSet.Contains(CompletedEventIdPrefix + saga.EventId);
            var hasDeadLetterFacet = latestDlq is not null
                && saga.Status != AccountCleanupSagaStatus.Completed;

            result.Add(new AccountCleanupSagaItemDto
            {
                UserId = saga.UserId,
                SagaStatus = saga.Status,
                DisplayStatus = hasDeadLetterFacet
                    ? AccountCleanupDisplayStatus.DeadLetter
                    : saga.Status,
                SourceEventId = saga.EventId,
                LastError = saga.LastError,
                ReplayCount = saga.ReplayCount,
                OutboxAttemptCount = outbox?.AttemptCount,
                OutboxStatus = outbox?.Status,
                DeadLetterDeliveryCount = latestDlq?.DeliveryCount,
                DeadLetterReasonCode = latestDlq?.ReasonCode,
                DeadLetterReason = latestDlq?.Reason,
                LatestDeadLetterAt = latestDlq?.CreatedAt,
                HasDeadLetter = latestDlq is not null,
                HasCompletedInboxEvidence = hasCompletedInbox,
                CreatedAt = saga.CreatedAt,
                UpdatedAt = saga.UpdatedAt,
                CompletedAt = saga.CompletedAt,
            });
        }

        return result;
    }

    private async Task EnsureInboxAsync(
        string eventId,
        long userId,
        string outcome,
        CancellationToken cancellationToken)
    {
        var exists = await db.AccountCleanupInbox
            .AnyAsync(x => x.EventId == eventId, cancellationToken);
        if (exists)
            return;

        db.AccountCleanupInbox.Add(new AccountCleanupInboxEntry
        {
            EventId = eventId,
            UserId = userId,
            Outcome = outcome,
            ProcessedAt = DateTimeOffset.UtcNow,
        });
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? "";
        return value.Length <= max ? value : value[..max];
    }
}
