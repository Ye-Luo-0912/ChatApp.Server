namespace Core.Models.Export;

/// <summary>AccountCleanupCompleted 消费 Inbox（按完成事件 EventId 去重）。</summary>
public sealed class AccountCleanupInboxEntry
{
    public string EventId { get; set; } = "";
    public long UserId { get; set; }
    public string Outcome { get; set; } = "";
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class AccountCleanupInboxOutcome
{
    public const string Completed = "completed";
    public const string DeadLetterMismatch = "dead_letter_mismatch";
    public const string DeadLetterInvalid = "dead_letter_invalid";
    public const string DeadLetterMissingExhausted = "dead_letter_missing_exhausted";
}
