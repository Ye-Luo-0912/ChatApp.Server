using Core.Caching;
using Core.Interfaces.Cache;

namespace ChatApp.Server.IntegrationTests.Support;

/// <summary>
/// 无操作缓存，供仅需数据库的集成测试使用。
/// </summary>
internal sealed class NoopCacheProvider : ICacheProvider
{
    public bool IsHealthy => true;

    public async Task<T?> GetAsync<T>(
        string key,
        Func<Task<T>>? valueFactory = null,
        TimeSpan? slidingExpiration = null,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
    {
        if (valueFactory is null)
            return default;
        return await valueFactory().ConfigureAwait(false);
    }

    public Task<string?> StringGetAsync(
        string key,
        Func<Task<string?>>? valueFactory = null,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
        => valueFactory is null ? Task.FromResult<string?>(null) : valueFactory();

    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? slidingExpiration = null,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task StringSetAsync(
        string key,
        string value,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> StringSetIfNotExistsAsync(
        string key,
        string value,
        TimeSpan? absoluteExpiration = null,
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

    public Task<long> StringIncrementAsync(
        string key,
        TimeSpan? absoluteExpirationWhenCreate = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(1L);

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RefreshAsync(string key, TimeSpan slidingExpiration, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<TimeSpan?> GetTimeToLiveAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult<TimeSpan?>(null);

    public Task<bool> ExistsAsync(string key) => Task.FromResult(false);

    public Task SetStringPayloadAsync<T>(
        string key,
        T value,
        TimeSpan absoluteExpiration,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<T?> GetStringPayloadAsync<T>(string key, CancellationToken cancellationToken = default)
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

    public Task KeyDeleteAsync(string key, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
