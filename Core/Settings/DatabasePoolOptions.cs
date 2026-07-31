namespace Core.Settings;

/// <summary>
/// PostgreSQL 连接池预算。API 与 Worker 通常是不同部署进程，使用 Role 选择各自上限；
/// 单进程运行时使用 All，预算为两者之和，避免后台任务无限挤占 API 连接。
/// </summary>
public sealed class DatabasePoolOptions
{
    public const string SectionName = "DatabasePool";

    /// <summary>Api、Worker 或 All。</summary>
    public string Role { get; init; } = "All";

    /// <summary>API 进程的最大连接数。</summary>
    public int ApiMaximumPoolSize { get; init; } = 48;

    /// <summary>Worker 进程的最大连接数。</summary>
    public int WorkerMaximumPoolSize { get; init; } = 16;

    /// <summary>每个进程预热的最小连接数。</summary>
    public int MinimumPoolSize { get; init; }

    /// <summary>当前进程实际使用的连接上限。</summary>
    public int EffectiveMaximumPoolSize => (Role ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "API" => ApiMaximumPoolSize,
        "WORKER" => WorkerMaximumPoolSize,
        "ALL" => checked(ApiMaximumPoolSize + WorkerMaximumPoolSize),
        _ => 0,
    };
}
