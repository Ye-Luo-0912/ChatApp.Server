namespace Core.Settings;

/// <summary>当前进程承载的职责。</summary>
public enum DatabaseProcessRole
{
    Api,
    Worker,
    All,
}

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

    /// <summary>API 进程的 EF command timeout。</summary>
    public int ApiCommandTimeoutSeconds { get; init; } = 5;

    /// <summary>Worker 进程的 EF command timeout。</summary>
    public int WorkerCommandTimeoutSeconds { get; init; } = 120;

    /// <summary>兼容单进程 All 角色的 EF command timeout。</summary>
    public int AllCommandTimeoutSeconds { get; init; } = 15;

    /// <summary>迁移命令独立使用的 timeout，不受 API 热路径预算影响。</summary>
    public int MigrationCommandTimeoutSeconds { get; init; } = 120;

    /// <summary>仅由 --migrate 启动路径设置。</summary>
    public bool UseMigrationTimeout { get; init; }

    /// <summary>当前进程实际使用的连接上限。</summary>
    public int EffectiveMaximumPoolSize => TryParseRole(Role, out var role) ? role switch
    {
        DatabaseProcessRole.Api => ApiMaximumPoolSize,
        DatabaseProcessRole.Worker => WorkerMaximumPoolSize,
        DatabaseProcessRole.All => checked(ApiMaximumPoolSize + WorkerMaximumPoolSize),
        _ => 0,
    } : 0;

    /// <summary>当前 DbContext 应使用的 command timeout（秒）。</summary>
    public int EffectiveCommandTimeoutSeconds
    {
        get
        {
            if (UseMigrationTimeout)
                return MigrationCommandTimeoutSeconds;

            return TryParseRole(Role, out var role) ? role switch
            {
                DatabaseProcessRole.Api => ApiCommandTimeoutSeconds,
                DatabaseProcessRole.Worker => WorkerCommandTimeoutSeconds,
                DatabaseProcessRole.All => AllCommandTimeoutSeconds,
                _ => 0,
            } : 0;
        }
    }

    public static bool IsSupportedRole(string? role)
        => TryParseRole(role, out _);

    public static DatabaseProcessRole ParseRole(string? role)
    {
        if (TryParseRole(role, out var parsed))
            return parsed;

        throw new ArgumentException("DatabasePool:Role 必须为 Api、Worker 或 All", nameof(role));
    }

    public static bool TryParseRole(string? role, out DatabaseProcessRole parsed)
    {
        if (string.Equals(role?.Trim(), "Api", StringComparison.OrdinalIgnoreCase))
        {
            parsed = DatabaseProcessRole.Api;
            return true;
        }

        if (string.Equals(role?.Trim(), "Worker", StringComparison.OrdinalIgnoreCase))
        {
            parsed = DatabaseProcessRole.Worker;
            return true;
        }

        if (string.Equals(role?.Trim(), "All", StringComparison.OrdinalIgnoreCase))
        {
            parsed = DatabaseProcessRole.All;
            return true;
        }

        parsed = default;
        return false;
    }
}
