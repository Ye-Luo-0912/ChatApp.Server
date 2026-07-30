using Core.Caching;
using Core.Interfaces.Cache;

namespace ChatApp.Server.IntegrationTests.Support;

/// <summary>
/// 无操作缓存，供仅需数据库的集成测试使用。
/// </summary>
internal sealed class NoopCacheProvider : ICacheValueStore, IAtomicCacheStore, ICacheSetStore
{
    public bool IsHealthy => true;

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        => Task.FromResult<T?>(default);

    public Task<IReadOnlyList<T?>> GetManyAsync<T>(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<T?>>(new T?[keys.Count]);

    public Task<string?> StringGetAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task StringSetAsync(
        string key,
        string value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> StringSetIfNotExistsAsync(
        string key,
        string value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> TryStringCompareAndDeleteAsync(
        string key,
        string expectedValue,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<bool> TryStringCompareAndExpireAsync(
        string key,
        string expectedValue,
        TimeSpan absoluteExpiration,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<bool> TryStringCompareAndSetAsync(
        string key,
        string expectedValue,
        string replacementValue,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<long> StringIncrementAsync(
        string key,
        TimeSpan expirationWhenCreate,
        CancellationToken cancellationToken = default)
        => Task.FromResult(1L);

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<T?> TryGetAndDeleteAsync<T>(
        string key, CancellationToken cancellationToken = default)
        => Task.FromResult<T?>(default);

    public Task SetManyAsync(
        IReadOnlyList<CacheSetRequest> writes,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<AtomicConsumeResult<TResult>> TryAtomicConsumeAsync<T, TResult>(
        string consumeKey,
        Func<T, AtomicConsumePlan<TResult>?> createPlan,
        CancellationToken cancellationToken = default)
        => Task.FromResult(AtomicConsumeResult<TResult>.Fail());

    public Task SetAddAsync(
        string key,
        string member,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SetRemoveAsync(string key, string member, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<string>> SetMembersAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

}
