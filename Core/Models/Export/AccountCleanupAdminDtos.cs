namespace Core.Models.Export;

/// <summary>
/// 运维 UI 展示状态：含 Saga 三态，以及有未修复死信时的 DeadLetter 分面。
/// </summary>
public static class AccountCleanupDisplayStatus
{
    public const string Pending = AccountCleanupSagaStatus.Pending;
    public const string Completed = AccountCleanupSagaStatus.Completed;
    public const string Failed = AccountCleanupSagaStatus.Failed;
    public const string DeadLetter = "DeadLetter";
}

/// <summary>账号清理 Saga 状态 / 修复中心列表项。</summary>
public sealed class AccountCleanupSagaItemDto
{
    public long UserId { get; init; }
    public string SagaStatus { get; init; } = "";
    /// <summary>UI 分面：Completed/Pending/Failed，或未完成且存在死信时为 DeadLetter。</summary>
    public string DisplayStatus { get; init; } = "";
    public string SourceEventId { get; init; } = "";
    public string? LastError { get; init; }
    public int ReplayCount { get; init; }
    public int? OutboxAttemptCount { get; init; }
    public short? OutboxStatus { get; init; }
    public long? DeadLetterDeliveryCount { get; init; }
    public string? DeadLetterReasonCode { get; init; }
    public string? DeadLetterReason { get; init; }
    public DateTimeOffset? LatestDeadLetterAt { get; init; }
    public bool HasDeadLetter { get; init; }
    public bool HasCompletedInboxEvidence { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed class AccountCleanupSagaListResponse
{
    public IReadOnlyList<AccountCleanupSagaItemDto> Items { get; init; } = [];
    public int Total { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
}

public enum AccountCleanupReplayOutcome
{
    Replayed = 0,
    NotFound = 1,
    AlreadyCompleted = 2,
    InvalidUser = 3,
}

public sealed class AccountCleanupReplayResponse
{
    public AccountCleanupReplayOutcome Outcome { get; init; }
    public string Message { get; init; } = "";
    public AccountCleanupSagaItemDto? Item { get; init; }
}

public enum AccountCleanupReconcileOutcome
{
    /// <summary>Inbox 已有 Completed 证据，已把 Saga 标为 Completed。</summary>
    MarkedCompletedFromInbox = 0,

    /// <summary>Outbox 已 Dead 且 Saga 仍 Pending，已标 Failed。</summary>
    MarkedFailedFromOutboxDead = 1,

    /// <summary>Saga 已是 Completed，无需动作。</summary>
    AlreadyCompleted = 2,

    /// <summary>无可用完成证据；通常需人工重放。</summary>
    NoEvidence = 3,

    NotFound = 4,
    InvalidUser = 5,
}

public sealed class AccountCleanupReconcileResponse
{
    public AccountCleanupReconcileOutcome Outcome { get; init; }
    public string Message { get; init; } = "";
    public AccountCleanupSagaItemDto? Item { get; init; }
}
