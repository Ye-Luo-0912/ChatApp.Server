namespace Core.Models.Export;

public sealed record RealtimeOutboxSummaryDto(
    long PendingCount,
    long DeadCount,
    long PublishedCount,
    long? OldestPendingAtMs,
    long? OldestPendingAgeMs,
    long? OldestDeadAtMs,
    int MaxPendingAttemptCount,
    long GeneratedAtMs);

public sealed record RealtimeOutboxItemDto(
    string EventId,
    short Status,
    string StatusName,
    short EventType,
    string EventTypeName,
    long TargetUserId,
    int AttemptCount,
    long CreatedAtMs,
    long NextAttemptAtMs,
    long? PublishedAtMs,
    string? LockedBy,
    long? LockedUntilMs,
    string? LastError,
    string? PayloadPreview);

public sealed record RealtimeOutboxListResponse(
    IReadOnlyList<RealtimeOutboxItemDto> Items,
    int Offset,
    int Limit,
    int Returned);

public sealed record RealtimeOutboxBatchReplayResult(
    int Requested,
    int Replayed,
    IReadOnlyList<string> Skipped);
