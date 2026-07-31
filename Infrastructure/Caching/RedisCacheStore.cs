using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Core.Caching;
using Core.Exceptions;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Infrastructure.Caching;

/// <summary>
/// Redis/Garnet 的轻量状态存储：统一 STRING + 原生 TTL，原子行为使用短 Lua 或事务。
/// 不隐式回源、不提供通用分布式锁，也不重试完成状态不明确的写操作。
/// </summary>
public sealed class RedisCacheStore : ICacheValueStore, IAtomicCacheStore, ICacheSetStore
{
    private const string CompareDeleteScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    private const string CompareExpireScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        end
        return 0
        """;

    private const string CompareSetScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          redis.call('PSETEX', KEYS[1], ARGV[3], ARGV[2])
          return 1
        end
        return 0
        """;

    private const string IncrementWithTtlScript = """
        local value = redis.call('INCR', KEYS[1])
        if value == 1 then
          redis.call('PEXPIRE', KEYS[1], ARGV[1])
        end
        return value
        """;

    private const string SetAddWithTtlScript = """
        local added = redis.call('SADD', KEYS[1], ARGV[1])
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        return added
        """;

    private static readonly Meter Meter = new("Infrastructure.Caching");
    private static readonly ActivitySource ActivitySource = new("Infrastructure.Caching.Redis");
    private static readonly Counter<long> CacheHits =
        Meter.CreateCounter<long>("cache.hits", description: "缓存命中次数");
    private static readonly Counter<long> CacheMisses =
        Meter.CreateCounter<long>("cache.misses", description: "缓存未命中次数");
    private static readonly Counter<long> CacheErrors =
        Meter.CreateCounter<long>("cache.errors", description: "缓存连接或超时错误次数");
    private static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>("cache.operation.duration", "ms", "缓存操作耗时");

    private readonly IDatabase _db;
    private readonly IConnectionMultiplexer _redis;
    private readonly ISerializer _serializer;
    private readonly ILogger<RedisCacheStore> _logger;
    private readonly RedisCacheOptions _options;

    public RedisCacheStore(
        IConnectionMultiplexer redis,
        ISerializer serializer,
        ILogger<RedisCacheStore> logger,
        IOptions<RedisCacheOptions> options)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _db = redis.GetDatabase();
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public bool IsHealthy => _redis.IsConnected;

    public async Task<T?> GetAsync<T>(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return default;

        var value = await ExecuteAsync(
            "value.get",
            () => _db.StringGetAsync(NormalizeKey(key)),
            cancellationToken).ConfigureAwait(false);
        return Deserialize<T>(value, key);
    }

    public async Task<IReadOnlyList<T?>> GetManyAsync<T>(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
            return [];

        var redisKeys = new RedisKey[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            if (string.IsNullOrEmpty(keys[i]))
                throw new ArgumentException("批量读取的键不能为空", nameof(keys));
            redisKeys[i] = NormalizeKey(keys[i]);
        }

        var values = await ExecuteAsync(
            "value.get_many",
            () => _db.StringGetAsync(redisKeys),
            cancellationToken).ConfigureAwait(false);

        var result = new T?[values.Length];
        for (var i = 0; i < values.Length; i++)
            result[i] = Deserialize<T>(values[i], keys[i]);
        return result;
    }

    public async Task SetAsync<T>(
        string key,
        T value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        ValidateWrite(key, value, expiration);
        var payload = Serialize(value, key);
        _ = await ExecuteAsync(
            "value.set",
            () => _db.StringSetAsync(NormalizeKey(key), payload, expiration),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> StringGetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        var value = await ExecuteAsync(
            "string.get",
            () => _db.StringGetAsync(NormalizeKey(key)),
            cancellationToken).ConfigureAwait(false);

        if (!value.HasValue)
        {
            CacheMisses.Add(1);
            return null;
        }

        CacheHits.Add(1);
        return value.ToString();
    }

    public async Task StringSetAsync(
        string key,
        string value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        ValidateWrite(key, value, expiration);
        _ = await ExecuteAsync(
            "string.set",
            () => _db.StringSetAsync(NormalizeKey(key), value, expiration),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return;

        _ = await ExecuteAsync(
            "key.delete",
            () => _db.KeyDeleteAsync(NormalizeKey(key)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>批量删除：单次 DEL 多键，减少往返。</summary>
    public async Task RemoveManyAsync(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
            return;

        var redisKeys = new RedisKey[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            if (!string.IsNullOrEmpty(keys[i]))
                redisKeys[i] = NormalizeKey(keys[i]);
        }

        _ = await ExecuteAsync(
            "key.delete_many",
            () => _db.KeyDeleteAsync(redisKeys),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> StringSetIfNotExistsAsync(
        string key,
        string value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        ValidateWrite(key, value, expiration);
        return await ExecuteAsync(
            "string.set_nx",
            () => _db.StringSetAsync(NormalizeKey(key), value, expiration, When.NotExists),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryStringCompareAndDeleteAsync(
        string key,
        string expectedValue,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        var result = await ExecuteAsync(
            "string.compare_delete",
            () => _db.ScriptEvaluateAsync(
                CompareDeleteScript,
                [NormalizeKey(key)],
                [expectedValue]),
            cancellationToken).ConfigureAwait(false);
        return (long)result == 1;
    }

    public async Task<bool> TryStringCompareAndExpireAsync(
        string key,
        string expectedValue,
        TimeSpan absoluteExpiration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return false;
        if (absoluteExpiration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(absoluteExpiration));

        var result = await ExecuteAsync(
            "string.compare_expire",
            () => _db.ScriptEvaluateAsync(
                CompareExpireScript,
                [NormalizeKey(key)],
                [expectedValue, ToMilliseconds(absoluteExpiration)]),
            cancellationToken).ConfigureAwait(false);
        return (long)result == 1;
    }

    public async Task<bool> TryStringCompareAndSetAsync(
        string key,
        string expectedValue,
        string replacementValue,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        ValidateWrite(key, replacementValue, expiration);
        var result = await ExecuteAsync(
            "string.compare_set",
            () => _db.ScriptEvaluateAsync(
                CompareSetScript,
                [NormalizeKey(key)],
                [expectedValue, replacementValue, ToMilliseconds(expiration)]),
            cancellationToken).ConfigureAwait(false);
        return (long)result == 1;
    }

    public async Task<long> StringIncrementAsync(
        string key,
        TimeSpan expirationWhenCreate,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("键不能为空", nameof(key));
        if (expirationWhenCreate <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expirationWhenCreate));

        var result = await ExecuteAsync(
            "string.increment",
            () => _db.ScriptEvaluateAsync(
                IncrementWithTtlScript,
                [NormalizeKey(key)],
                [ToMilliseconds(expirationWhenCreate)]),
            cancellationToken).ConfigureAwait(false);
        return (long)result;
    }

    public async Task<T?> TryGetAndDeleteAsync<T>(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return default;

        var value = await ExecuteAsync(
            "value.get_delete",
            () => _db.StringGetDeleteAsync(NormalizeKey(key)),
            cancellationToken).ConfigureAwait(false);

        if (!value.HasValue)
            return default;

        var bytes = (byte[]?)value;
        return bytes is { Length: > 0 } ? DeserializeBytes<T>(bytes, key) : default;
    }

    public async Task SetManyAsync(
        IReadOnlyList<CacheSetRequest> writes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return;

        var prepared = new PreparedWrite[writes.Count];
        for (var i = 0; i < writes.Count; i++)
            prepared[i] = PrepareWrite(writes[i]);

        var transaction = _db.CreateTransaction();
        foreach (var write in prepared)
            _ = transaction.StringSetAsync(write.Key, write.Payload, write.Expiration);

        var committed = await ExecuteAsync(
            "transaction.set_many",
            () => transaction.ExecuteAsync(),
            cancellationToken).ConfigureAwait(false);

        if (!committed)
        {
            throw new CacheUnavailableException(
                "批量写入事务未提交",
                new InvalidOperationException("MULTI/EXEC aborted"));
        }
    }

    public async Task<AtomicConsumeResult<TResult>> TryAtomicConsumeAsync<T, TResult>(
        string consumeKey,
        Func<T, AtomicConsumePlan<TResult>?> createPlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createPlan);
        if (string.IsNullOrEmpty(consumeKey))
            return AtomicConsumeResult<TResult>.Fail();

        var fullKey = NormalizeKey(consumeKey);
        var original = await ExecuteAsync(
            "atomic_consume.read",
            () => _db.StringGetAsync(fullKey),
            cancellationToken).ConfigureAwait(false);
        if (!original.HasValue)
            return AtomicConsumeResult<TResult>.Fail();

        var current = Deserialize<T>(original, consumeKey);
        if (current is null)
            return AtomicConsumeResult<TResult>.Fail();

        var plan = createPlan(current);
        if (plan is null)
            return AtomicConsumeResult<TResult>.Fail();

        var preparedWrites = new PreparedWrite[plan.Writes.Count];
        for (var i = 0; i < plan.Writes.Count; i++)
            preparedWrites[i] = PrepareWrite(plan.Writes[i]);

        var transaction = _db.CreateTransaction();
        transaction.AddCondition(Condition.StringEqual(fullKey, original));
        _ = transaction.KeyDeleteAsync(fullKey);

        foreach (var deleteKey in plan.AdditionalKeysToDelete)
        {
            if (!string.IsNullOrEmpty(deleteKey))
                _ = transaction.KeyDeleteAsync(NormalizeKey(deleteKey));
        }

        foreach (var write in preparedWrites)
            _ = transaction.StringSetAsync(write.Key, write.Payload, write.Expiration);

        var committed = await ExecuteAsync(
            "atomic_consume.commit",
            () => transaction.ExecuteAsync(),
            cancellationToken).ConfigureAwait(false);

        return committed
            ? AtomicConsumeResult<TResult>.Ok(plan.Result)
            : AtomicConsumeResult<TResult>.Fail();
    }

    public async Task<long[]> EvaluateScriptAsync(
        string script,
        IReadOnlyList<string> keys,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(args);

        var redisKeys = new RedisKey[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            redisKeys[i] = NormalizeKey(keys[i]);

        var redisArgs = new RedisValue[args.Count];
        for (var i = 0; i < args.Count; i++)
            redisArgs[i] = args[i];

        var result = await ExecuteAsync(
            "script.evaluate",
            () => _db.ScriptEvaluateAsync(script, redisKeys, redisArgs),
            cancellationToken).ConfigureAwait(false);

        if (result.IsNull)
            return [];

        var arr = (RedisResult[]?)result;
        if (arr is null || arr.Length == 0)
            return [];

        var longs = new long[arr.Length];
        for (var i = 0; i < arr.Length; i++)
            longs[i] = (long)arr[i];
        return longs;
    }

    public async Task SetAddAsync(
        string key,
        string member,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(member))
            return;

        if (absoluteExpiration is not { } expiration)
        {
            _ = await ExecuteAsync(
                "set.add",
                () => _db.SetAddAsync(NormalizeKey(key), member),
                cancellationToken).ConfigureAwait(false);
            return;
        }
        if (expiration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(absoluteExpiration));

        var result = await ExecuteAsync(
            "set.add_expire",
            () => _db.ScriptEvaluateAsync(
                SetAddWithTtlScript,
                [NormalizeKey(key)],
                [member, ToMilliseconds(expiration)]),
            cancellationToken).ConfigureAwait(false);
        _ = result;
    }

    public async Task SetRemoveAsync(
        string key,
        string member,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(member))
            return;

        _ = await ExecuteAsync(
            "set.remove",
            () => _db.SetRemoveAsync(NormalizeKey(key), member),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> SetMembersAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return [];

        var members = await ExecuteAsync(
            "set.members",
            () => _db.SetMembersAsync(NormalizeKey(key)),
            cancellationToken).ConfigureAwait(false);

        if (members.Length == 0)
            return [];

        var result = new string[members.Length];
        for (var i = 0; i < members.Length; i++)
            result[i] = members[i].ToString();
        return result;
    }

    /// <summary>批量 SREM：单次移除多个成员，减少往返。</summary>
    public async Task SetRemoveManyAsync(
        string key,
        IReadOnlyList<string> members,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (string.IsNullOrEmpty(key) || members.Count == 0)
            return;

        var values = new RedisValue[members.Count];
        for (var i = 0; i < members.Count; i++)
            values[i] = members[i];

        _ = await ExecuteAsync(
            "set.remove_many",
            () => _db.SetRemoveAsync(NormalizeKey(key), values),
            cancellationToken).ConfigureAwait(false);
    }

    private string NormalizeKey(string key) =>
        CacheKeyBuilder.WithPrefix(_options.KeyPrefix, key);

    private RedisValue Serialize<T>(T value, string key)
    {
        try
        {
            return _serializer.Serialize(value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "缓存序列化失败 KeyLength={KeyLength}", key.Length);
            throw new CacheSerializationException("对象序列化失败", ex);
        }
    }

    private T? Deserialize<T>(RedisValue value, string key)
    {
        if (!value.HasValue)
        {
            CacheMisses.Add(1);
            return default;
        }

        CacheHits.Add(1);
        var bytes = (byte[]?)value;
        return bytes is { Length: > 0 } ? DeserializeBytes<T>(bytes, key) : default;
    }

    private T? DeserializeBytes<T>(byte[] bytes, string key)
    {
        try
        {
            return _serializer.Deserialize<T>(bytes);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "缓存数据损坏 KeyLength={KeyLength}", key.Length);
            throw new CacheCorruptedException("缓存数据损坏", ex);
        }
    }

    private PreparedWrite PrepareWrite(CacheSetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateWrite(request.Key, request.Value, request.Expiration);
        return new PreparedWrite(
            NormalizeKey(request.Key),
            Serialize(request.Value, request.Key),
            request.Expiration);
    }

    private static void ValidateWrite<T>(string key, T value, TimeSpan expiration)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("键不能为空", nameof(key));
        ArgumentNullException.ThrowIfNull(value);
        if (expiration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiration), "过期时间必须为正数");
    }

    private static long ToMilliseconds(TimeSpan value) =>
        Math.Max(1L, (long)Math.Ceiling(value.TotalMilliseconds));

    private async Task<T> ExecuteAsync<T>(
        string operation,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var activity = ActivitySource.StartActivity("Redis", ActivityKind.Client);
        activity?.SetTag("db.system", "redis");
        activity?.SetTag("db.operation.name", operation);
        var started = Stopwatch.GetTimestamp();
        try
        {
            // 命令发出后等待 Redis 自身的 AsyncTimeout。对原子写入使用 WaitAsync
            // 会让调用方先收到取消，但命令仍可能在服务端成功，形成额外的不确定完成状态。
            return await action().ConfigureAwait(false);
        }
        catch (RedisConnectionException ex)
        {
            CacheErrors.Add(1);
            _logger.LogWarning(ex, "Redis 连接失败 Operation={Operation}", operation);
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
        catch (RedisTimeoutException ex)
        {
            CacheErrors.Add(1);
            _logger.LogWarning(ex, "Redis 操作超时 Operation={Operation}", operation);
            throw new CacheUnavailableException("Redis 服务超时", ex);
        }
        finally
        {
            OperationDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("cache.operation", operation));
        }
    }

    private readonly record struct PreparedWrite(
        RedisKey Key,
        RedisValue Payload,
        TimeSpan Expiration);
}
