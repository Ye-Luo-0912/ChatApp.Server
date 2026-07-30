namespace Core.Settings;

public sealed class NotificationOutboxOptions
{
    public const string SectionName = "NotificationOutbox";

    /// <summary>每轮领取条数。</summary>
    public int BatchSize { get; set; } = 20;

    // 并发已统一到 WorkerConcurrencyOptions（全局预算 + 每类 Worker 独立配置）。

    /// <summary>空闲轮询间隔（秒）。</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>积压采样间隔（秒）；写入 backlog gauge。</summary>
    public int BacklogSampleSeconds { get; set; } = 30;
}

public sealed class PasswordHashingOptions
{
    public const string SectionName = "PasswordHashing";

    /// <summary>同时进行的 BCrypt Verify/Hash 上限，防止打满 CPU。</summary>
    public int MaxConcurrentOperations { get; set; } = 4;

    /// <summary>获取闸门的最长等待（毫秒）；超时则快速拒绝。</summary>
    public int AcquireTimeoutMilliseconds { get; set; } = 200;
}
