namespace Core.Models.Email;

public class EmailOutboxItem
{
    public long Id { get; set; }
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsHtml { get; set; }
    public EmailOutboxStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
}
