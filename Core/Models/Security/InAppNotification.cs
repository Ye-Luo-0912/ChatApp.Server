namespace Core.Models.Security;

public sealed class InAppNotification
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>来源 Outbox Id；唯一，防止重试重复插入站内通知。</summary>
    public long? SourceOutboxId { get; set; }
}
