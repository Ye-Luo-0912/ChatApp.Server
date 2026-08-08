namespace Core.Settings;

public class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Secret { get; init; } = string.Empty;
    public string Issuer { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public short AccessTokenExpirationMinutes { get; init; } = 60;
    public byte RefreshTokenLength { get; init; } = 32;

    public int RefreshTokenExpirationDays { get; set; } = 3;

    // ── L1 访问令牌缓存（减少认证热路径的 Redis 往返） ──

    /// <summary>L1 内存缓存是否启用（默认启用）。</summary>
    public bool TokenL1CacheEnabled { get; init; } = true;

    /// <summary>L1 缓存最大条目数。超过时按 LRU 淘汰。</summary>
    public int TokenL1CacheMaxEntries { get; init; } = 10_000;

    /// <summary>L1 正缓存 TTL 上限（秒）。实际 TTL = min(此值, 令牌剩余寿命)。</summary>
    public int TokenL1CacheTtlSeconds { get; init; } = 5;

    /// <summary>L1 负缓存 TTL（毫秒），防止无效令牌频繁击穿到 Redis。</summary>
    public int TokenL1CacheNegativeTtlMs { get; init; } = 200;

    /// <summary>用户认证 fence 本机 L1 最大条目数。</summary>
    public int AuthFenceL1CacheMaxEntries { get; init; } = 10_000;

    /// <summary>用户认证 fence 本机 L1 TTL；默认 1 秒，对应普通会话撤销 SLA。</summary>
    public int AuthFenceL1CacheTtlMilliseconds { get; init; } = 1_000;

    /// <summary>
    /// 用户认证 fence 在 Garnet 中的 TTL。必须明显长于本机 L1 TTL，
    /// 否则两级缓存会同步过期，L1 miss 会重新落到 PostgreSQL。
    /// 权威安全变更会先删除该值并写入版本地板，再广播 L1 驱逐；TTL
    /// 只约束漏掉失效消息时的最终收敛窗口。默认 60 秒把正常 L1 miss
    /// 限定为 Garnet 往返；安全变更会主动删除该值，因此该 TTL 只代表
    /// 失效广播/缓存写入异常时的最坏回退窗口。
    /// </summary>
    public int AuthFenceDistributedTtlSeconds { get; init; } = 60;

    /// <summary>每个用户允许保留的活跃会话数，超出时淘汰最旧会话。</summary>
    public int MaxActiveSessionsPerUser { get; init; } = 10;

    /// <summary>一次登录最多因会话 churn 淘汰的旧会话数，避免单次请求产生无界撤销工作。</summary>
    public int SessionChurnCleanupBatchSize { get; init; } = 10;
}
