namespace Core.Models.Export;

/// <summary>
/// Server 侧账号清理完成事件死信（非法 / 耗尽重试）。
/// JetStream 消息 ACK 后落库，供运维对账与人工重放。
/// </summary>
public sealed class AccountCleanupDeadLetter
{
    public long Id { get; set; }
    public string EventId { get; set; } = "";
    public long UserId { get; set; }
    public string ReasonCode { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? PayloadJson { get; set; }
    public long? DeliveryCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class AccountCleanupDeadLetterReason
{
    public const string EventIdMismatch = "event_id_mismatch";
    public const string InvalidCompletedEventId = "invalid_completed_event_id";
    public const string MissingSagaExhausted = "missing_saga_exhausted";
}
