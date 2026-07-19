using Core.Caching;

namespace Core.Interfaces.Cache;

/// <summary>
/// 缓存访问抽象，统一约束读取、写入、过期控制和基础状态查询能力。
/// </summary>
public interface ICacheProvider
{
    /// <summary>
    /// 从缓存中异步获取指定键的值，如果未找到且提供了valueFactory，则通过该委托回源数据。
    /// </summary>
    /// <typeparam name="T">要获取的数据类型。</typeparam>
    /// <param name="key">缓存项的唯一标识符。</param>
    /// <param name="valueFactory">当缓存中不存在对应键的值时，用于生成新值的异步函数。可选参数，默认为null。</param>
    /// <param name="slidingExpiration">滑动过期时间，即在给定时间内如果没有被访问则过期。可选参数，默认为null。</param>
    /// <param name="absoluteExpiration">绝对过期时间，到达这个时间点后缓存将自动失效。可选参数，默认为null。</param>
    /// <param name="cancellationToken">取消令牌，用于请求取消操作。默认为无取消令牌。</param>
    /// <returns>返回与指定键关联的值或由valueFactory生成的新值；如果缓存和valueFactory均无法提供值，则返回默认值。</returns>
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
    /// 仅当键不存在时写入字符串（SET NX），用于冷却锁等互斥场景。
    /// </summary>
    /// <returns>写入成功为 <see langword="true"/>；键已存在为 <see langword="false"/>。</returns>
    Task<bool> StringSetIfNotExistsAsync(
        string key,
        string value,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 原子 compare-and-delete：仅当字符串值等于 <paramref name="expectedValue"/> 时删除键。
    /// </summary>
    Task<bool> TryStringCompareAndDeleteAsync(
        string key,
        string expectedValue,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 对字符串计数器执行 INCR，并在首次创建时设置过期时间。
    /// </summary>
    Task<long> StringIncrementAsync(
        string key,
        TimeSpan? absoluteExpirationWhenCreate = null,
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

    /// <summary>
    /// 原子地消费一个缓存键：读取并反序列化后由 <paramref name="createPlan"/> 决定是否继续；
    /// 仅当键仍持有读取时的原始值时，才删除旧键并写入替换条目（CAS）。
    /// <para>
    /// 并发调用同一键时严格只有一个计划会成功；其余返回 <see langword="default"/>。
    /// 适用于刷新令牌轮换等必须“校验 + 消费 + 写入”同一步完成的场景。
    /// </para>
    /// </summary>
    /// <returns>CAS 成功时 <see cref="AtomicConsumeResult{TResult}.Succeeded"/> 为 <see langword="true"/>。</returns>
    Task<AtomicConsumeResult<TResult>> TryAtomicConsumeAsync<T, TResult>(
        string consumeKey,
        Func<T, AtomicConsumePlan<TResult>?> createPlan,
        CancellationToken cancellationToken = default);
}