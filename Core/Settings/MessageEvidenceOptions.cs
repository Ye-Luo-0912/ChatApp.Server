namespace Core.Settings;

/// <summary>消息审核证据：直连 Realtime 消息库或经 NATS 总线查询。</summary>
public sealed class MessageEvidenceOptions
{
    public const string SectionName = "MessageEvidence";

    /// <summary>Realtime Postgres 连接串；优先于 NATS（同库直读，低延迟）。</summary>
    public string? RealtimeConnectionString { get; set; }

    public string Schema { get; set; } = "realtime";

    public int TimeoutMilliseconds { get; set; } = 2_000;

    public int CacheSeconds { get; set; } = 30;

    /// <summary>连续失败次数达到后熔断。</summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    public int CircuitBreakerDurationSeconds { get; set; } = 30;
}
