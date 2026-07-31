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

    /// <summary>每个用户允许保留的活跃会话数，超出时淘汰最旧会话。</summary>
    public int MaxActiveSessionsPerUser { get; init; } = 10;

    /// <summary>一次登录最多因会话 churn 淘汰的旧会话数，避免单次请求产生无界撤销工作。</summary>
    public int SessionChurnCleanupBatchSize { get; init; } = 10;
}
