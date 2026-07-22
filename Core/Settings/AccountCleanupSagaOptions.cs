namespace Core.Settings;

public sealed class AccountCleanupSagaOptions
{
    public const string SectionName = "AccountCleanupSaga";

    /// <summary>
    /// Pending 超过该小时数则标记 Failed（0 = 禁用超时失败）。
    /// 覆盖完成事件丢失 / MaxDeliver 耗尽。
    /// </summary>
    public int PendingTimeoutHours { get; init; } = 72;

    /// <summary>超时扫描间隔（分钟）。</summary>
    public int StalePollIntervalMinutes { get; init; } = 30;

    /// <summary>
    /// 尚无 Saga 时的最大投递次数（乱序窗口）；超过后进 DLQ 并 ACK。
    /// </summary>
    public int MaxMissingSagaDeliveries { get; init; } = 5;

    /// <summary>MissingSaga NAK 延迟（秒）。</summary>
    public int MissingSagaNakDelaySeconds { get; init; } = 5;
}