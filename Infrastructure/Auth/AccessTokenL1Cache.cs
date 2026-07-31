using System.Threading;
using Core.Models.Token;
using Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Infrastructure.Auth;

/// <summary>
/// 访问令牌 L1 内存缓存：减少认证热路径的 Redis 往返。
/// <para>
/// 策略：
/// <list type="bullet">
///   <item>正缓存 TTL = min(配置秒数, 令牌剩余寿命)，默认 5 秒</item>
///   <item>负缓存 TTL = 200ms，防止无效令牌频繁击穿到 Redis</item>
///   <item>有界容量：超过上限时按 LRU 淘汰</item>
///   <item>撤销时主动驱逐：RevokeAccessTokenAsync / RevokeSessionAsync</item>
/// </list>
/// </para>
/// </summary>
internal sealed class AccessTokenL1Cache : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly int _maxPositiveTtlSeconds;
    private readonly int _negativeTtlMs;

    private long _hits;
    private long _misses;

    /// <summary>L1 正缓存命中次数。</summary>
    public long Hits => Interlocked.Read(ref _hits);
    /// <summary>L1 未命中次数（需回退到 Redis）。</summary>
    public long Misses => Interlocked.Read(ref _misses);

    public AccessTokenL1Cache(int maxEntries, int maxPositiveTtlSeconds, int negativeTtlMs)
    {
        _maxPositiveTtlSeconds = maxPositiveTtlSeconds;
        _negativeTtlMs = negativeTtlMs;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = maxEntries,
            CompactionPercentage = 0.10,
            ExpirationScanFrequency = TimeSpan.FromSeconds(1),
        });
    }

    /// <summary>
    /// 查找 L1 缓存。
    /// <para>返回 (Found, Data)：</para>
    /// <list type="bullet">
    ///   <item>Found=true, Data≠null → 正缓存命中，直接返回令牌数据</item>
    ///   <item>Found=true, Data=null → 负缓存命中，令牌确认不存在，无需查 Redis</item>
    ///   <item>Found=false → 未命中，需查询 Redis</item>
    /// </list>
    /// </summary>
    public (bool Found, AccessTokenData? Data) TryGet(string key)
    {
        if (_cache.TryGetValue(key, out var entry) && entry is CacheEntry ce)
        {
            if (ce is PositiveEntry { Data: var data })
            {
                // 令牌可能在缓存期间过期——此时视为未命中并驱逐
                if (data.IsExpired)
                {
                    _cache.Remove(key);
                    Interlocked.Increment(ref _misses);
                    return (false, null);
                }
                Interlocked.Increment(ref _hits);
                return (true, data);
            }
            // 负缓存命中
            Interlocked.Increment(ref _hits);
            return (true, null);
        }
        Interlocked.Increment(ref _misses);
        return (false, null);
    }

    /// <summary>写入正缓存条目。TTL = min(配置上限, 令牌剩余寿命)。</summary>
    public void SetPositive(string key, AccessTokenData data)
    {
        var remainingMs = data.ExpiresAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (remainingMs <= 0) return;

        var ttl = remainingMs > _maxPositiveTtlSeconds * 1000L
            ? TimeSpan.FromSeconds(_maxPositiveTtlSeconds)
            : TimeSpan.FromMilliseconds(remainingMs);

        _cache.Set(key, new PositiveEntry(data), new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            Size = 1,
        });
    }

    /// <summary>写入负缓存条目（令牌不存在），短 TTL 防止击穿。</summary>
    public void SetNegative(string key)
    {
        _cache.Set(key, NegativeEntry.Instance, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds(_negativeTtlMs),
            Size = 1,
        });
    }

    /// <summary>驱逐指定键（正/负缓存均移除）。撤销令牌时调用。</summary>
    public void Evict(string key)
    {
        _cache.Remove(key);
        AuthSecurityMetrics.RecordTokenL1("eviction");
    }

    public void Dispose() => _cache.Dispose();

    // ── 内部条目类型 ──

    private abstract record CacheEntry;
    private sealed record PositiveEntry(AccessTokenData Data) : CacheEntry;
    private sealed record NegativeEntry : CacheEntry
    {
        public static readonly NegativeEntry Instance = new();
    }
}
