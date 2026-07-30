using Core.Caching;

namespace Core.Interfaces.Cache;

/// <summary>普通值缓存。读取不会隐式回源或获取分布式锁。</summary>
public interface ICacheValueStore
{
    Task<T?> GetAsync<T>(
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>批量读取同类型值；实现应流水线化，避免逐键往返。</summary>
    Task<IReadOnlyList<T?>> GetManyAsync<T>(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default);

    Task<string?> StringGetAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task SetAsync<T>(
        string key,
        T value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default);

    Task StringSetAsync(
        string key,
        string value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    bool IsHealthy { get; }
}

/// <summary>原子状态操作。完成状态不明确的命令不得在实现层自动重试。</summary>
public interface IAtomicCacheStore
{
    Task<bool> StringSetIfNotExistsAsync(
        string key,
        string value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default);

    Task<bool> TryStringCompareAndDeleteAsync(
        string key,
        string expectedValue,
        CancellationToken cancellationToken = default);

    Task<bool> TryStringCompareAndExpireAsync(
        string key,
        string expectedValue,
        TimeSpan absoluteExpiration,
        CancellationToken cancellationToken = default);

    /// <summary>仅当当前字符串等于期望值时，原子替换值并设置新 TTL。</summary>
    Task<bool> TryStringCompareAndSetAsync(
        string key,
        string expectedValue,
        string replacementValue,
        TimeSpan expiration,
        CancellationToken cancellationToken = default);

    Task<long> StringIncrementAsync(
        string key,
        TimeSpan expirationWhenCreate,
        CancellationToken cancellationToken = default);

    /// <summary>原子 GETDEL；并发调用同一键时至多一个调用者获得载荷。</summary>
    Task<T?> TryGetAndDeleteAsync<T>(
        string key,
        CancellationToken cancellationToken = default);

    Task SetManyAsync(
        IReadOnlyList<CacheSetRequest> writes,
        CancellationToken cancellationToken = default);

    /// <summary>CAS 消费值，并在同一事务中删除关联键、写入替换条目。</summary>
    Task<AtomicConsumeResult<TResult>> TryAtomicConsumeAsync<T, TResult>(
        string consumeKey,
        Func<T, AtomicConsumePlan<TResult>?> createPlan,
        CancellationToken cancellationToken = default);
}

/// <summary>Redis SET 索引操作。</summary>
public interface ICacheSetStore
{
    Task SetAddAsync(
        string key,
        string member,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default);

    Task SetRemoveAsync(
        string key,
        string member,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> SetMembersAsync(
        string key,
        CancellationToken cancellationToken = default);
}
