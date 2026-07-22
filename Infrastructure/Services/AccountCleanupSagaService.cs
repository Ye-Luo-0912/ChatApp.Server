using ChatApp.Realtime.Abstractions.Events;
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

        saga.Status = AccountCleanupSagaStatus.Completed;
        saga.CompletedAt = DateTimeOffset.UtcNow;
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

    public async Task<bool> TryReplayAsync(long userId, CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
            return false;

        var saga = await db.AccountCleanupSagas
            .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
        if (saga is null)
            return false;
        if (saga.Status is not (AccountCleanupSagaStatus.Failed or AccountCleanupSagaStatus.Pending))
            return false;

        var completedEventId = CompletedEventIdPrefix + saga.EventId;
        var staleInbox = await db.AccountCleanupInbox
            .Where(x => x.EventId == completedEventId
                        && x.Outcome != AccountCleanupInboxOutcome.Completed)
            .ToListAsync(cancellationToken);
        if (staleInbox.Count > 0)
            db.AccountCleanupInbox.RemoveRange(staleInbox);

        saga.Status = AccountCleanupSagaStatus.Pending;
        saga.CompletedAt = null;
        saga.LastError = null;
        saga.CreatedAt = DateTimeOffset.UtcNow;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
            "AccountCleanupSaga 人工重放。UserId={UserId}；EventId={EventId}",
            userId,
            saga.EventId);
        return true;
    }

    public static string? TryGetSourceEventId(string completedEventId)
    {
        if (completedEventId.StartsWith(CompletedEventIdPrefix, StringComparison.Ordinal))
            return completedEventId[CompletedEventIdPrefix.Length..];
        return null;
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
