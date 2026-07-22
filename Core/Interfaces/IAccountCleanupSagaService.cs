using Core.Models.Export;

namespace Core.Interfaces;

public interface IAccountCleanupSagaService
{
    /// <summary>
    /// 应用 AccountCleanupCompleted：校验 cleanup-done:{sourceEventId} 后标 Completed（幂等）。
    /// 校验失败不会推进 Saga。
    /// </summary>
    Task<AccountCleanupApplyResult> TryCompleteAsync(
        long userId,
        string completedEventId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 将超时仍 Pending 的 Saga 标为 Failed，便于运维对账 / 人工重放。
    /// </summary>
    Task<int> FailStalePendingAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);

    /// <summary>非法 / 耗尽重试的完成事件写入 Server 侧 DLQ，并登记 Inbox。</summary>
    Task RecordDeadLetterAsync(
        string eventId,
        long userId,
        string? payloadJson,
        string reasonCode,
        string reason,
        ulong? deliveryCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 人工重放：Failed（或超时 Pending）Saga 重置为 Pending，并重新投递 UserAccountDeleted。
    /// </summary>
    Task<bool> TryReplayAsync(long userId, CancellationToken cancellationToken = default);
}
