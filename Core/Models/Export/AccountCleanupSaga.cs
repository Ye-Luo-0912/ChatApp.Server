namespace Core.Models.Export;

public static class AccountCleanupSagaStatus
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

/// <summary>
/// 跨服务账号清理 Saga：Server 发出 UserAccountDeleted 后写入 Pending；
/// Realtime 回传 AccountCleanupCompleted 后由 AccountCleanupSagaWorker 标为 Completed；
/// 超时仍 Pending 时标为 Failed（无专用 DLQ 时的对账兜底）。
/// </summary>
public sealed class AccountCleanupSaga
{
    public long UserId { get; set; }
    public string EventId { get; set; } = "";
    public string Status { get; set; } = AccountCleanupSagaStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastError { get; set; }
}
