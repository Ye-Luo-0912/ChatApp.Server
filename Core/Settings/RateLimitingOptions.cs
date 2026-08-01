namespace Core.Settings;

/// <summary>限流策略可配置项；Performance 环境可显著放宽以便容量压测。</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public int AuthLoginPermitLimit { get; set; } = 10;
    public int AuthLoginWindowSeconds { get; set; } = 60;

    public int AuthRegisterPermitLimit { get; set; } = 5;
    public int AuthRegisterWindowSeconds { get; set; } = 60;

    public int AuthRefreshPermitLimit { get; set; } = 30;
    public int AuthRefreshWindowSeconds { get; set; } = 60;

    public int AuthEmailPermitLimit { get; set; } = 5;
    public int AuthEmailWindowSeconds { get; set; } = 60;

    public int UserEmailChangePermitLimit { get; set; } = 3;
    public int UserEmailChangeWindowSeconds { get; set; } = 900;

    /// <summary>敏感操作（step-up / 可信设备签发）每用户窗口限额。</summary>
    public int UserSensitivePermitLimit { get; set; } = 10;
    public int UserSensitiveWindowSeconds { get; set; } = 900;

    /// <summary>
    /// Redis 不可用时限流的显式失败策略：
    /// <c>true</c> = 放行以保可用；<c>false</c>（默认）= 拒绝以保安全。
    /// </summary>
    public bool FailOpenWhenRedisUnavailable { get; set; }

    /// <summary>
    /// 多维 Lua 限流使用的策略级 Redis Cluster 槽数。必须固定为一个槽，
    /// 否则同一维度和不同维度组合时会被拆成彼此独立的限额。
    /// </summary>
    public int ClusterShardCount { get; set; } = 1;
}
