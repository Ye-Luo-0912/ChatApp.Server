namespace Core.Interfaces.Cache;

/// <summary>
/// 缓存访问抽象，统一约束读取、写入、过期控制和基础状态查询能力。
/// </summary>
public interface ICacheProvider
{
    /// <summary>
    /// 读取指定键对应的缓存对象。
    /// </summary>
    /// <remarks>
    /// 当传入回源委托时，缓存未命中会先执行委托，再将结果写回缓存。
    /// </remarks>
    Task<T?> GetAsync<T>(
        string key,
        Func<Task<T>>? valueFactory = null,
        TimeSpan? slidingExpiration = null,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取指定键对应的字符串缓存。
    /// </summary>
    /// <remarks>
    /// 适合验证码、令牌等简单字符串值，也支持在未命中时通过委托回源。
    /// </remarks>
    Task<string?> StringGetAsync(
        string key,
        Func<Task<string?>>? valueFactory = null,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 写入任意类型的缓存值，并支持滑动过期和绝对过期。
    /// </summary>
    Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? slidingExpiration = null,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 写入字符串缓存值。
    /// </summary>
    /// <remarks>
    /// 当前主要用于验证码等场景，要求过期时间尽量精确。
    /// </remarks>
    Task StringSetAsync(
        string key,
        string value,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除指定键对应的缓存条目。
    /// </summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 刷新缓存键的过期时间。
    /// </summary>
    /// <remarks>
    /// 主要面向带滑动过期语义的缓存项，普通字符串缓存通常不需要调用。
    /// </remarks>
    Task RefreshAsync(string key, TimeSpan slidingExpiration, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取缓存键当前剩余的生存时间。
    /// </summary>
    /// <remarks>
    /// 常用于验证码限流这类需要根据剩余 TTL 做业务判断的场景。
    /// </remarks>
    Task<TimeSpan?> GetTimeToLiveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 检查指定键是否存在。
    /// </summary>
    Task<bool> ExistsAsync(string key);

    /// <summary>
    /// 当前缓存提供程序是否处于可用状态。
    /// </summary>
    bool IsHealthy { get; }
}