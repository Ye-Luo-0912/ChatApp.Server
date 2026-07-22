namespace Core.Models.Export;

/// <summary>AccountCleanupCompleted 应用到 Saga 的结果（驱动 ACK / NAK / DLQ）。</summary>
public enum AccountCleanupApplyResult
{
    /// <summary>首次标为 Completed。</summary>
    Completed = 0,

    /// <summary>Saga 已 Completed（幂等）。</summary>
    AlreadyCompleted = 1,

    /// <summary>Inbox 已见过该完成事件（消费方去重）。</summary>
    DuplicateDelivery = 2,

    /// <summary>尚无 Saga 行：可能乱序，应有限 NAK。</summary>
    MissingSaga = 3,

    /// <summary>cleanup-done 前缀与 Saga.EventId 不一致：非法，进 DLQ。</summary>
    EventIdMismatch = 4,

    /// <summary>完成事件 EventId 缺少 cleanup-done: 前缀：非法，进 DLQ。</summary>
    InvalidCompletedEventId = 5,
}
