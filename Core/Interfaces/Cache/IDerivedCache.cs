namespace Core.Interfaces.Cache;

/// <summary>
/// 派生缓存：用于好友关系、地理位置等可从数据库重建的可缓存数据。
/// </summary>
/// <remarks>
/// <para>固定策略：</para>
/// <list type="bullet">
///   <item>Garnet 连接失败：视为未命中（fail-open），不传播异常。</item>
///   <item>反序列化失败：删除损坏键并视为未命中。</item>
///   <item>不使用分布式锁。</item>
/// </list>
/// </remarks>
public interface IDerivedCache
{
    /// <summary>尝试获取缓存值。Found=false 表示未命中（含连接失败）。</summary>
    Task<CacheLookup<T>> TryGetAsync<T>(
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>批量获取缓存值，单次往返。未命中或连接失败的槽位返回 Miss。</summary>
    Task<IReadOnlyList<CacheLookup<T>>> TryGetManyAsync<T>(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default);

    /// <summary>写入缓存值并设置 TTL。</summary>
    Task SetAsync<T>(
        string key,
        T value,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);

    /// <summary>流水线批量写入同一 TTL 的派生值，避免冷缓存逐项网络往返。</summary>
    Task SetManyAsync<T>(
        IReadOnlyList<KeyValuePair<string, T>> values,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);


    /// <summary>批量删除多个键。用于关系变更后双向失效。</summary>
    Task RemoveManyAsync(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default);
}

/// <summary>缓存查找结果。</summary>
public readonly record struct CacheLookup<T>(bool Found, T? Value)
{
    public static CacheLookup<T> Miss => new(false, default);
    public static CacheLookup<T> Hit(T value) => new(true, value);
}
