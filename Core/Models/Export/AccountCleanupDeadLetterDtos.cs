namespace Core.Models.Export;

public sealed record AccountCleanupDeadLetterDto(
    long Id,
    string EventId,
    long UserId,
    string ReasonCode,
    string Reason,
    long? DeliveryCount,
    DateTimeOffset CreatedAt);
