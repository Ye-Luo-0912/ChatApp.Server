using Core.Caching;
using Core.Interfaces.Cache;

namespace ChatApp.Server.IntegrationTests.Support;

/// <summary>
/// 无操作缓存，供仅需数据库的集成测试使用。
/// 同时实现派生缓存与一次性状态接口，避免测试替身碎片化。
/// </summary>
internal sealed class NoopCacheProvider
    : ICacheValueStore, IAtomicCacheStore, ICacheSetStore, IDerivedCache, IOneTimeStateStore
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

    public Task RemoveManyAsync(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
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

    public Task<long[]> EvaluateScriptAsync(
        string script,
        IReadOnlyList<string> keys,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
        => Task.FromResult<long[]>([]);

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

    public Task SetRemoveManyAsync(
        string key,
        IReadOnlyList<string> members,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    // ─────────────────────────────────────────────────────────
    // IDerivedCache：派生缓存（fail-open 语义，noop 始终未命中）
    // ─────────────────────────────────────────────────────────

    public Task<CacheLookup<T>> TryGetAsync<T>(
        string key,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CacheLookup<T>.Miss);

    public Task<IReadOnlyList<CacheLookup<T>>> TryGetManyAsync<T>(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        var missAll = new CacheLookup<T>[keys.Count];
        Array.Fill(missAll, CacheLookup<T>.Miss);
        return Task.FromResult<IReadOnlyList<CacheLookup<T>>>(missAll);
    }

    // SetAsync<T> 与 RemoveManyAsync 由 ICacheValueStore 的同名实现满足，
    public Task SetManyAsync<T>(
        IReadOnlyList<KeyValuePair<string, T>> values,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    // 两接口签名一致，无需重复实现。

    // ─────────────────────────────────────────────────────────
    // IOneTimeStateStore：一次性状态（fail-closed 语义，noop 始终无状态）
    // ─────────────────────────────────────────────────────────

    public Task IssueAsync<T>(
        string key,
        T payload,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<T?> TryConsumeAsync<T>(
        string key,
        CancellationToken cancellationToken = default)
        => Task.FromResult<T?>(default);

    public Task RestoreAsync<T>(
        string key,
        T payload,
        DateTimeOffset originalExpiresAt,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> TryConsumeIfEqualAsync(
        string key,
        string expectedValue,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<string?> PeekAsync(
        string key,
        CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}
