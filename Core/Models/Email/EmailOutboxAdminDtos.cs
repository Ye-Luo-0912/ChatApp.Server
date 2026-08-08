namespace Core.Models.Email;

public sealed record EmailOutboxAdminItemDto(
    long Id,
    string To,
    string Subject,
    string? EmailType,
    int AttemptCount,
    string? LastError,
    DateTime UpdatedAt,
    DateTime CreatedAt);
