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
    /// 人工重放：Failed / Pending Saga 重置为 Pending 并重新投递 UserAccountDeleted。
    /// Completed 拒绝（不安全）；返回明确 Outcome 供运维 UI。
    /// </summary>
    Task<AccountCleanupReplayResponse> TryReplayAsync(
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 对账：用 Inbox Completed 证据推进 Stuck Saga；或用 Outbox Dead 标 Failed。
    /// </summary>
    Task<AccountCleanupReconcileResponse> TryReconcileAsync(
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>单用户状态（含死信 / Outbox 相关字段）。</summary>
    Task<AccountCleanupSagaItemDto?> GetStatusAsync(
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列表：可按 Saga 状态或 DeadLetter 分面过滤，offset/limit 分页。
    /// </summary>
    Task<AccountCleanupSagaListResponse> ListAsync(
        string? status,
        long? userId,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AccountCleanupDeadLetterDto>> ListDeadLettersAsync(
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<AccountCleanupDeadLetterDto?> GetDeadLetterAsync(
        long id,
        CancellationToken cancellationToken = default);
}
