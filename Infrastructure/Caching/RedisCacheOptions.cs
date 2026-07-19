namespace Infrastructure.Caching;

/// <summary>
/// Garnet（Redis 兼容）缓存配置，从 appsettings.json 的 "GarnetCache" 节读取。
/// </summary>
public sealed class RedisCacheOptions
{
    /// <summary>对应 appsettings.json 的配置节名称。</summary>
    public const string SectionName = "GarnetCache";

    /// <summary>Key 命名空间前缀，默认 "cache:"。</summary>
    public string KeyPrefix { get; set; } = "cache:";

    /// <summary>默认滑动过期时间，默认 30 分钟。</summary>
    public TimeSpan DefaultSlidingExpiration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>过期抖动百分比（0~1），防止缓存雪崩，默认 5%。</summary>
    public double ExpirationJitterPercent { get; set; } = 0.05;

    /// <summary>等待分布式锁的最长时间，默认 10 秒。</summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>锁本身的过期时长，防止持锁方崩溃后死锁，默认 5 秒。</summary>
    public TimeSpan DefaultLockExpiry { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>空值缓存的过期时间，用于防止缓存穿透，默认 5 分钟。</summary>
    public TimeSpan NullValueExpiration { get; set; } = TimeSpan.FromMinutes(5);
}