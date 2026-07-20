using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
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
/// Redis 缓存提供程序实现，支持泛型数据存储和多种过期策略。
/// </summary>
public class RedisCaching : ICacheProvider
{
    /// Redis 数据库实例
    private readonly IDatabase _db;
    // redis 连接实例，主要用于健康检查和获取服务器信息等操作，实际读写通过 IDatabase 完成
    private readonly IConnectionMultiplexer _redis;
    private readonly ISerializer _serializer;
    private readonly ILogger<RedisCaching> _logger;
    private readonly RedisCacheOptions _options;
    private readonly Counter<int> _cacheHits;
    private readonly Counter<int> _cacheMisses;
    private readonly Histogram<double> _operationDuration;
    
    private const string RedisGetName = "Redis.Get";

    // 字段名与标记常量统一来自 Core.Caching.CacheConstants，便于跨项目共享
    private static readonly string ValueField              = CacheConstants.ValueField;
    private static readonly string AbsoluteExpirationField = CacheConstants.AbsoluteExpirationField;
    private static readonly string SlidingExpirationField  = CacheConstants.SlidingExpirationField;
    private static readonly string NullValueMarker         = CacheConstants.NullValueMarker;
    private static readonly Meter Meter = new("Infrastructure.Caching");
    // 添加 ActivitySource
    private static readonly ActivitySource ActivitySource = new("Infrastructure.Caching.Redis");
    public bool IsHealthy => _redis.IsConnected;

    private string NormalizeKey(string key) => CacheKeyBuilder.WithPrefix(_options.KeyPrefix, key);

    /// <summary>
    /// 初始化 Redis 缓存服务
    /// </summary>
    /// <param name="redis"></param>
    /// <param name="serializer"></param>
    /// <param name="logger"></param>
    /// <param name="options"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public RedisCaching(
        IConnectionMultiplexer redis,
        ISerializer serializer,
        ILogger<RedisCaching> logger,
        IOptions<RedisCacheOptions>? options)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _db = redis.GetDatabase();
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        // 初始化监控指标
        _cacheHits = Meter.CreateCounter<int>("cache.hits", "次", "缓存命中次数");
        _cacheMisses = Meter.CreateCounter<int>("cache.misses", "次", "缓存未命中次数");
        _operationDuration = Meter.CreateHistogram<double>(
            "cache.operation.duration", "ms", "缓存操作耗时");
    }

    public async Task<T?> GetAsync<T>(string key, Func<Task<T>>? valueFactory = null,
        TimeSpan? slidingExpiration = null, TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            _logger.LogWarning("尝试获取缓存时使用了空键");
            return default;
        }

        using var activity = ActivitySource.StartActivity(RedisGetName);
        var stopwatch = Stopwatch.StartNew();
        var fullKey = NormalizeKey(key);

        activity?.AddTag("cache.full_key", fullKey);
        try
        {
            // 获取缓存值
            var redisValue = await ExecuteWithRetry(() => _db.HashGetAllAsync(fullKey), cancellationToken).ConfigureAwait(false);

            if (redisValue.Length > 0)
            {
                return await ProcessEnvelopeHitAsync<T>(redisValue, fullKey, cancellationToken).ConfigureAwait(false);
            }
            // 处理缓存未命中
            _cacheMisses.Add(1);
            _logger.LogDebug("缓存未命中: {Key}", fullKey);
            if (valueFactory == null) return default;

            // 获取分布式锁，防止缓存击穿
            var lockKey = CacheKeyBuilder.LockKey(fullKey);
            var lockValue = Guid.CreateVersion7().ToString("N");
            if (await AcquireLockAsync(lockKey, lockValue, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    // 双重检查
                    var recheckValue = await ExecuteWithRetry(() => _db.HashGetAsync(fullKey, ValueField), cancellationToken).ConfigureAwait(false);
                    if (!recheckValue.IsNullOrEmpty)
                    {
                        if (recheckValue != NullValueMarker) 
                            return SmartDeserialize<T>(recheckValue);
                        
                        _logger.LogDebug("空值穿透防御命中（双重检查）: {Key}", fullKey);
                        return default;

                    }

                    // 生成实际值
                    var value = await valueFactory().ConfigureAwait(false);
                    var expiration = CalculateExpiration(slidingExpiration, absoluteExpiration);

                    if (value is null)
                    {
                        // 空值缓存
                        await SetHashNullValueAsync(fullKey, expiration, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await SetValueAsync(fullKey, value, slidingExpiration, absoluteExpiration, cancellationToken).ConfigureAwait(false);
                    }
                    return value;
                }
                finally
                {
                    await ReleaseLockAsync(lockKey, lockValue, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                // 等待锁释放后重试获取缓存
                for (var i = 0; i < 3; i++)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    var retryValue = await ExecuteWithRetry(() => _db.HashGetAsync(fullKey, ValueField), cancellationToken).ConfigureAwait(false);
                    
                    if (!retryValue.HasValue) 
                        continue;
                    
                    return retryValue == NullValueMarker ? default : _serializer.Deserialize<T>((byte[])retryValue!);
                }
            }
        }

        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "Redis 连接失败: {Key}", key);
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "反序列化失败: {Key}", key);
            throw new CacheCorruptedException("缓存数据损坏", ex);
        }
        finally
        {
            _operationDuration.Record(stopwatch.ElapsedMilliseconds);
        }
        return default;
    }

    /// <summary>
    /// 过期时间仅支持绝对过期
    /// </summary>
    /// <param name="key"></param>
    /// <param name="valueFactory"></param>
    /// <param name="absoluteExpiration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="CacheUnavailableException"></exception>
    /// <exception cref="CacheCorruptedException"></exception>
    public async Task<string?> StringGetAsync(string key, Func<Task<string?>>? valueFactory = null, TimeSpan? absoluteExpiration = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            _logger.LogWarning("尝试获取缓存时使用了空键");
            return null;
        }

        using var activity = ActivitySource.StartActivity("Redis.GetString");
        var stopwatch = Stopwatch.StartNew();
        var fullKey = NormalizeKey(key);

        activity?.AddTag("cache.full_key", fullKey);
        try
        {
            var redisValue = await ExecuteWithRetry(() => _db.StringGetAsync(fullKey), cancellationToken).ConfigureAwait(false);

            if (redisValue.HasValue)
            {
                if(redisValue == NullValueMarker)
                {
                    _cacheHits.Add(1);
                    _logger.LogDebug("空值穿透防御命中: {Key}", fullKey);
                    return null;
                }

                _cacheHits.Add(1);
                _logger.LogDebug("缓存命中: {Key}", fullKey);
                return redisValue.ToString();
            }

            _cacheMisses.Add(1);
            _logger.LogDebug("缓存未命中: {Key}", fullKey);
            if (valueFactory == null) return null;

            // 获取分布式锁，防止缓存击穿
            var lockKey = CacheKeyBuilder.LockKey(fullKey);
            var lockValue = Guid.CreateVersion7().ToString("N");
            if (await AcquireLockAsync(lockKey, lockValue, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    // 双重检查
                    var recheckValue = await ExecuteWithRetry(() => _db.StringGetAsync(fullKey), cancellationToken).ConfigureAwait(false);
                    if (recheckValue.HasValue)
                    {
                        if(recheckValue == NullValueMarker)
                            return null;

                        return recheckValue.ToString();
                    }
                        

                    // 生成实际值
                    var value = await valueFactory().ConfigureAwait(false);
                    var expiration = CalculateExpiration(null, absoluteExpiration);

                    if (value == null)
                    {
                        await SetNullValueAsync(fullKey, expiration, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await _db.StringSetAsync(fullKey, value, expiration).ConfigureAwait(false);
                    }

                    return value;
                }
                finally
                {
                    await ReleaseLockAsync(lockKey, lockValue, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                // 等待锁释放后重试获取缓存
                for (int i = 0; i < 3; i++)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    var retryValue = await ExecuteWithRetry(() => _db.StringGetAsync(fullKey), cancellationToken).ConfigureAwait(false);
                    if (retryValue.HasValue)
                    {
                        if (retryValue == NullValueMarker)
                            return null;

                        return retryValue.ToString();
                    }
                }
            }
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "Redis 连接失败: {Key}", key);
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "序列化失败: {Key}", key);
            throw new CacheCorruptedException("序列化失败! 请检查数据", ex);
        }
        finally
        {
            _operationDuration.Record(stopwatch.ElapsedMilliseconds);
        }

        return null;
    }

    /// <summary>
    /// 将指定的值存储到缓存中。
    /// </summary>
    /// <typeparam name="T">要存储的值的类型。</typeparam>
    /// <param name="key">缓存项的键。</param>
    /// <param name="value">要存储的值。</param>
    /// <param name="slidingExpiration">滑动过期时间。如果在该时间段内没有访问，则缓存项将被移除。</param>
    /// <param name="absoluteExpiration">绝对过期时间。到达这个时间点后，缓存项将被移除。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>一个表示异步操作的任务。</returns>
    /// <exception cref="CacheUnavailableException">当Redis服务不可用时抛出。</exception>
    public async Task SetAsync<T>(string key, T value, TimeSpan? slidingExpiration = null,
        TimeSpan? absoluteExpiration = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            _logger.LogWarning("尝试设置缓存时使用了空键");
            return;
        }

        using var activity = ActivitySource.StartActivity("Redis.Set");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var fullKey = NormalizeKey(key);
            var expiration = CalculateExpiration(slidingExpiration, absoluteExpiration);

            if (value == null)
            {
                await SetHashNullValueAsync(fullKey, expiration, cancellationToken);
            }
            else
            {
                await SetValueAsync(fullKey, value, slidingExpiration,absoluteExpiration, cancellationToken);
            }

            _logger.LogDebug("缓存设置成功: {Key} (过期时间: {Expiration})", key, expiration);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "Redis 连接失败: {Key}", key);
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
        finally
        {
            _operationDuration.Record(stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 异步设置字符串值到缓存中。
    /// </summary>
    /// <param name="key">要设置的缓存项的键。</param>
    /// <param name="value">要存储的字符串值。</param>
    /// <param name="absoluteExpiration">绝对过期时间，如果未指定，则使用默认滑动过期时间。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <exception cref="CacheUnavailableException">当 Redis 服务不可用时抛出。</exception>
    /// <returns>表示异步操作的任务。</returns>
    public async Task StringSetAsync(string key, string value, TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            _logger.LogWarning("尝试设置缓存时使用了空键");
            return;
        }
        using var activity = ActivitySource.StartActivity("Redis.SetString");
        var stopwatch = Stopwatch.StartNew();

        var fullKey = NormalizeKey(key);
        // 字符串缓存当前主要承载验证码等场景，这里保留精确 TTL，不额外加抖动。
        var expiration = absoluteExpiration ?? _options.DefaultSlidingExpiration;

        try
        {
            await _db.StringSetAsync(fullKey, value, expiration).WaitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("缓存设置成功: {Key} (过期时间: {Expiration})", key, expiration);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "Redis 连接失败: {Key}", key);
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
        finally
        {
            _operationDuration.Record(stopwatch.ElapsedMilliseconds);
        }
    }

    /// <inheritdoc />
    public async Task<bool> StringSetIfNotExistsAsync(
        string key,
        string value,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            _logger.LogWarning("尝试 SET NX 时使用了空键");
            return false;
        }

        var fullKey = NormalizeKey(key);
        var expiration = absoluteExpiration ?? _options.DefaultSlidingExpiration;

        try
        {
            return await ExecuteWithRetry(
                    () => _db.StringSetAsync(fullKey, value, expiration, When.NotExists),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "Redis 连接失败（SET NX）: {Key}", key);
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryStringCompareAndDeleteAsync(
        string key,
        string expectedValue,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        var fullKey = NormalizeKey(key);

        try
        {
            var tran = _db.CreateTransaction();
            tran.AddCondition(Condition.StringEqual(fullKey, expectedValue));
            _ = tran.KeyDeleteAsync(fullKey);
            return await tran.ExecuteAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "Redis 连接失败（compare-and-delete）: {Key}", key);
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryStringCompareAndExpireAsync(
        string key,
        string expectedValue,
        TimeSpan absoluteExpiration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        var fullKey = NormalizeKey(key);
        var ttlMs = (long)Math.Max(1, absoluteExpiration.TotalMilliseconds);

        try
        {
            var result = await ExecuteWithRetry(
                    () => _db.ScriptEvaluateAsync(
                        """
                        if redis.call('GET', KEYS[1]) == ARGV[1] then
                          return redis.call('PEXPIRE', KEYS[1], ARGV[2])
                        end
                        return 0
                        """,
                        [fullKey],
                        [expectedValue, ttlMs]),
                    cancellationToken)
                .ConfigureAwait(false);
            return (int)result == 1;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "Redis 连接失败（compare-and-expire）: {Key}", key);
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
    }

    /// <inheritdoc />
    public async Task<long> StringIncrementAsync(
        string key,
        TimeSpan? absoluteExpirationWhenCreate = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("键不能为空", nameof(key));

        var fullKey = NormalizeKey(key);
        var ttlMs = absoluteExpirationWhenCreate is { } ttl
            ? (long)Math.Max(1, ttl.TotalMilliseconds)
            : 0L;

        try
        {
            // INCR + 首次 PEXPIRE 原子化，避免中途崩溃留下永不过期的限流键
            var result = await ExecuteWithRetry(
                    () => _db.ScriptEvaluateAsync(
                        """
                        local v = redis.call('INCR', KEYS[1])
                        if v == 1 and tonumber(ARGV[1]) > 0 then
                          redis.call('PEXPIRE', KEYS[1], ARGV[1])
                        end
                        return v
                        """,
                        [fullKey],
                        [ttlMs]),
                    cancellationToken)
                .ConfigureAwait(false);

            return (long)result;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "Redis 连接失败（INCR）: {Key}", key);
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
    }

    /// <summary>
    /// 从缓存中移除指定键的条目。
    /// </summary>
    /// <param name="key">要删除的缓存条目的键。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>一个表示异步删除操作的任务。</returns>
    /// <exception cref="CacheUnavailableException">当 Redis 服务不可用时抛出。</exception>
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            _logger.LogWarning("尝试删除缓存时使用了空键");
            return;
        }
        var fullKey = NormalizeKey(key);
        try
        {
            // 删除本身是幂等操作，键不存在也按正常路径结束。
            var deleted = await ExecuteWithRetry(() => _db.KeyDeleteAsync(fullKey), cancellationToken).ConfigureAwait(false);
            _logger.LogDebug(deleted ? "缓存删除成功: {Key}" : "键不存在: {Key}", key);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "Redis 连接失败: {Key}", key);
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
    }

    /// <summary>
    /// 刷新缓存过期时间,
    /// 仅适用于Hash元信息缓存（带abdExp，aldExp）字段缓存， 不适用于普通字符串缓存
    /// </summary>
    public async Task RefreshAsync(string key, TimeSpan slidingExpiration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            _logger.LogWarning("尝试刷新缓存时使用了空键");
            return;
        }

        var fullKey = NormalizeKey(key);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // 先读出绝对过期时间，避免滑动续期把键延长到上限之外。
            var absExpValue = await ExecuteWithRetry(() => _db.HashGetAsync(fullKey, AbsoluteExpirationField), cancellationToken).ConfigureAwait(false);

            var newTtl = slidingExpiration;

            // 
            if (absExpValue.HasValue && absExpValue.TryParse(out long absTicks))
            {
                var absoluteExpiration = new DateTimeOffset(absTicks, TimeSpan.Zero);
                var maxRemaining = absoluteExpiration - DateTimeOffset.UtcNow;

                if (maxRemaining <= TimeSpan.Zero)
                {
                    // 已经过了绝对过期点时，直接清掉这个键，避免继续续期。
                    await _db.KeyDeleteAsync(fullKey).ConfigureAwait(false);
                    return;
                }

                // 最终 TTL 不能超过绝对过期剩余时间。
                if (newTtl > maxRemaining) newTtl = maxRemaining;
            }


            await ExecuteWithRetry(() => _db.KeyExpireAsync(fullKey, newTtl), cancellationToken).ConfigureAwait(false);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "Redis 连接失败: {Key}", key);
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "刷新缓存失败: {Key}", key);
            throw new CacheUnavailableException("缓存操作失败", ex);
        }
        finally
        {
            _operationDuration.Record(stopwatch.ElapsedMilliseconds);
        }
       
    }

    /// <summary>
    /// 获取指定键的剩余生存时间。
    /// </summary>
    /// <param name="key">缓存项的键。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>返回一个表示剩余生存时间的时间跨度（TimeSpan），如果键不存在或发生错误，则返回 null。</returns>
    /// <exception cref="CacheUnavailableException">当 Redis 服务不可用时抛出。</exception>
    public async Task<TimeSpan?> GetTimeToLiveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            _logger.LogWarning("尝试获取缓存剩余生存时间时使用了空键");
            return null;
        }

        var fullKey = NormalizeKey(key);

        try
        {
            // 直接读取 Redis 当前 TTL，给验证码这类按剩余时间做判断的场景使用。
            return await _db.KeyTimeToLiveAsync(fullKey).WaitAsync(cancellationToken);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "Redis 连接失败: {Key}", key);
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
    }

    /// <summary>
    /// 检查指定的键是否存在于缓存中。
    /// </summary>
    /// <param name="key">要检查的缓存项的键。</param>
    /// <returns>如果键存在，则返回true；否则返回false。</returns>
    public async Task<bool> ExistsAsync(string key)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        return await _db.KeyExistsAsync(NormalizeKey(key));
    }

    /// <inheritdoc />
    public async Task SetAddAsync(
        string key,
        string member,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(member))
            return;

        var fullKey = NormalizeKey(key);
        await ExecuteWithRetry(() => _db.SetAddAsync(fullKey, member), cancellationToken).ConfigureAwait(false);
        if (absoluteExpiration is { } ttl)
            await ExecuteWithRetry(() => _db.KeyExpireAsync(fullKey, ttl), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetRemoveAsync(string key, string member, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(member))
            return;

        await ExecuteWithRetry(() => _db.SetRemoveAsync(NormalizeKey(key), member), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> SetMembersAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return [];

        var members = await ExecuteWithRetry(() => _db.SetMembersAsync(NormalizeKey(key)), cancellationToken)
            .ConfigureAwait(false);
        if (members.Length == 0)
            return [];

        var result = new List<string>(members.Length);
        foreach (var member in members)
        {
            if (member.HasValue)
                result.Add(member.ToString());
        }

        return result;
    }

    /// <inheritdoc />
    public Task KeyDeleteAsync(string key, CancellationToken cancellationToken = default)
        => RemoveAsync(key, cancellationToken);

    /// <inheritdoc />
    public async Task<AtomicConsumeResult<TResult>> TryAtomicConsumeAsync<T, TResult>(
        string consumeKey,
        Func<T, AtomicConsumePlan<TResult>?> createPlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createPlan);

        if (string.IsNullOrEmpty(consumeKey))
        {
            _logger.LogWarning("尝试原子消费时使用了空键");
            return AtomicConsumeResult<TResult>.Fail();
        }

        using var activity = ActivitySource.StartActivity("Redis.AtomicConsume");
        var stopwatch = Stopwatch.StartNew();
        var fullKey = NormalizeKey(consumeKey);
        activity?.AddTag("cache.full_key", fullKey);

        try
        {
            // 先读取信封，做过期与业务校验；真正的互斥由下方 HashEqual CAS 保证。
            var hashEntries = await ExecuteWithRetry(
                () => _db.HashGetAllAsync(fullKey), cancellationToken).ConfigureAwait(false);

            if (hashEntries.Length == 0)
                return AtomicConsumeResult<TResult>.Fail();

            var valueEntry = hashEntries.FirstOrDefault(x => x.Name == ValueField).Value;
            var absExpEntry = hashEntries.FirstOrDefault(x => x.Name == AbsoluteExpirationField).Value;

            if (!valueEntry.HasValue || valueEntry == NullValueMarker)
                return AtomicConsumeResult<TResult>.Fail();

            if (absExpEntry.HasValue && absExpEntry.TryParse(out long absTicks))
            {
                var absTime = new DateTimeOffset(absTicks, TimeSpan.Zero);
                if (DateTimeOffset.UtcNow > absTime)
                {
                    await ExecuteWithRetry(() => _db.KeyDeleteAsync(fullKey), cancellationToken)
                        .ConfigureAwait(false);
                    return AtomicConsumeResult<TResult>.Fail();
                }
            }

            var current = SmartDeserialize<T>(valueEntry);
            if (current is null)
                return AtomicConsumeResult<TResult>.Fail();

            var plan = createPlan(current);
            if (plan is null)
                return AtomicConsumeResult<TResult>.Fail();

            var preparedWrites = new List<PreparedWrite>(plan.Writes.Count);
            foreach (var write in plan.Writes)
            {
                if (string.IsNullOrEmpty(write.Key))
                    throw new ArgumentException("原子写入条目的键不能为空", nameof(createPlan));

                preparedWrites.Add(PrepareWrite(write));
            }

            var tran = _db.CreateTransaction();
            // 仅当 value 仍等于读取时的原始字节时才提交，保证并发下只有一个消费者成功。
            tran.AddCondition(Condition.HashEqual(fullKey, ValueField, valueEntry));

            _ = tran.KeyDeleteAsync(fullKey);
            foreach (var deleteKey in plan.AdditionalKeysToDelete)
            {
                if (string.IsNullOrEmpty(deleteKey))
                    continue;
                _ = tran.KeyDeleteAsync(NormalizeKey(deleteKey));
            }

            foreach (var write in preparedWrites)
            {
                if (write.AsString)
                {
                    _ = tran.StringSetAsync(write.FullKey, write.StringPayload!);
                    _ = tran.KeyExpireAsync(write.FullKey, write.Ttl);
                }
                else
                {
                    _ = tran.HashSetAsync(write.FullKey, write.Entries!);
                    _ = tran.KeyExpireAsync(write.FullKey, write.Ttl);
                }
            }

            var committed = await tran.ExecuteAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!committed)
            {
                _logger.LogDebug("原子消费 CAS 失败（已被并发消费）: {Key}", fullKey);
                return AtomicConsumeResult<TResult>.Fail();
            }

            _logger.LogDebug(
                "原子消费成功: {Key}, Deletes={DeleteCount}, Writes={WriteCount}",
                fullKey, plan.AdditionalKeysToDelete.Count + 1, preparedWrites.Count);

            return AtomicConsumeResult<TResult>.Ok(plan.Result);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "Redis 连接失败（原子消费）: {Key}", consumeKey);
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "反序列化失败（原子消费）: {Key}", consumeKey);
            throw new CacheCorruptedException("缓存数据损坏", ex);
        }
        finally
        {
            _operationDuration.Record(stopwatch.ElapsedMilliseconds);
        }
    }

    /// <inheritdoc />
    public async Task SetStringPayloadAsync<T>(
        string key,
        T value,
        TimeSpan absoluteExpiration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return;

        var fullKey = NormalizeKey(key);
        try
        {
            var bytes = _serializer.Serialize(value);
            await ExecuteWithRetry(
                    () => _db.StringSetAsync(fullKey, bytes, absoluteExpiration),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "Redis 连接失败（StringPayload SET）: {Key}", key);
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not CacheUnavailableException)
        {
            _logger.LogError(ex, "序列化失败（StringPayload SET）: {Key}", key);
            throw new CacheSerializationException("对象序列化失败", ex);
        }
    }

    /// <inheritdoc />
    public async Task<T?> GetStringPayloadAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return default;

        var fullKey = NormalizeKey(key);
        try
        {
            var value = await ExecuteWithRetry(() => _db.StringGetAsync(fullKey), cancellationToken)
                .ConfigureAwait(false);
            if (value.IsNullOrEmpty)
                return default;

            return _serializer.Deserialize<T>((byte[])value!);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "Redis 连接失败（StringPayload GET）: {Key}", key);
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "反序列化失败（StringPayload GET）: {Key}", key);
            throw new CacheCorruptedException("缓存数据损坏", ex);
        }
    }

    /// <inheritdoc />
    public async Task SetManyAsync(
        IReadOnlyList<CacheSetRequest> writes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return;

        try
        {
            var prepared = writes.Select(PrepareWrite).ToList();
            var tran = _db.CreateTransaction();
            foreach (var write in prepared)
            {
                if (write.AsString)
                {
                    _ = tran.StringSetAsync(write.FullKey, write.StringPayload!);
                    _ = tran.KeyExpireAsync(write.FullKey, write.Ttl);
                }
                else
                {
                    _ = tran.HashSetAsync(write.FullKey, write.Entries!);
                    _ = tran.KeyExpireAsync(write.FullKey, write.Ttl);
                }
            }

            var ok = await tran.ExecuteAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!ok)
                throw new CacheUnavailableException("批量写入事务未提交", new InvalidOperationException("MULTI/EXEC aborted"));
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "Redis 连接失败（SetMany）");
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
    }

    private PreparedWrite PrepareWrite(CacheSetRequest request)
    {
        var fullKey = NormalizeKey(request.Key);
        var ttl = CalculateExpiration(request.SlidingExpiration, request.AbsoluteExpiration);

        if (request.AsRedisString)
        {
            var payload = request.Value is string s
                ? System.Text.Encoding.UTF8.GetBytes(s)
                : _serializer.Serialize(request.Value);
            return new PreparedWrite(fullKey, null, payload, ttl, AsString: true);
        }

        var entries = new List<HashEntry>(3);
        var data = request.Value;

        if (data is string str)
            entries.Add(new HashEntry(ValueField, str));
        else
        {
            var type = data.GetType();
            if (type.IsPrimitive || type.IsValueType)
                entries.Add(new HashEntry(ValueField, data.ToString()!));
            else
                entries.Add(new HashEntry(ValueField, _serializer.Serialize(data)));
        }

        if (request.AbsoluteExpiration.HasValue)
        {
            entries.Add(new HashEntry(
                AbsoluteExpirationField,
                DateTimeOffset.UtcNow.Add(request.AbsoluteExpiration.Value).Ticks));
        }

        if (request.SlidingExpiration.HasValue)
        {
            entries.Add(new HashEntry(
                SlidingExpirationField,
                request.SlidingExpiration.Value.Ticks));
        }

        return new PreparedWrite(fullKey, entries.ToArray(), null, ttl, AsString: false);
    }

    private readonly record struct PreparedWrite(
        string FullKey,
        HashEntry[]? Entries,
        byte[]? StringPayload,
        TimeSpan Ttl,
        bool AsString);

    /// <summary>
    /// 计算最终的过期时间，考虑滑动过期、绝对过期和默认值，并添加随机抖动以防止缓存雪崩
    /// </summary>
    private TimeSpan CalculateExpiration(TimeSpan? sliding, TimeSpan? absolute)
    {
        TimeSpan baseExpiration;

        if(sliding.HasValue && absolute.HasValue)
        {
            baseExpiration = sliding.Value < absolute.Value ? sliding.Value : absolute.Value;
        }
        else
        {
            baseExpiration = sliding ?? absolute ?? _options.DefaultSlidingExpiration;
        }
        return AddJitter(baseExpiration);
    }

    /// <summary>
    /// 为过期时间添加随机抖动
    /// </summary>
    /// <param name="expiration"></param>
    /// <returns></returns>
    private TimeSpan AddJitter(TimeSpan expiration)
    {
        if (expiration <= TimeSpan.Zero) return expiration;

        var jitter = _options.ExpirationJitterPercent * expiration.Ticks;
        var randomJitter = (long)(Random.Shared.NextDouble() * jitter * 2 - jitter);
        var newTicks = Math.Max(expiration.Ticks + randomJitter, 0);
        return TimeSpan.FromTicks(newTicks);
    }

    /// <summary>
    /// 设置指定键的值到缓存中，并可选地设置滑动或绝对过期时间。
    /// </summary>
    /// <typeparam name="T">要存储的数据类型。</typeparam>
    /// <param name="fullKey">数据在缓存中的完整键名。</param>
    /// <param name="data">要存储的数据对象。</param>
    /// <param name="slidingExpiration">滑动过期时间，如果在这段时间内没有访问，则该条目将被移除。可以为null表示不使用滑动过期。</param>
    /// <param name="absoluteExpiration">绝对过期时间，从现在开始计算，超过此时间后条目将被移除。可以为null表示不使用绝对过期。</param>
    /// <param name="ct">用于取消操作的令牌。</param>
    /// <exception cref="CacheSerializationException">当序列化数据失败时抛出。</exception>
    private async Task SetValueAsync<T>(string fullKey, T data, TimeSpan? slidingExpiration,
        TimeSpan? absoluteExpiration, CancellationToken ct)
    {
        try
        {
            var entries = new List<HashEntry>();

            if (typeof(T) == typeof(string))
            {
                entries.Add(new HashEntry(ValueField, (string)(object)data!));
            }
            else if (typeof(T).IsPrimitive || typeof(T).IsValueType)
            {
                entries.Add(new HashEntry(ValueField, data!.ToString()!));
            }
            else
            {
                entries.Add(new HashEntry(ValueField, _serializer.Serialize(data)));
            }

            if (absoluteExpiration.HasValue)
                entries.Add(new HashEntry(AbsoluteExpirationField, DateTimeOffset.UtcNow.Add(absoluteExpiration.Value).Ticks));

            if (slidingExpiration.HasValue)
                entries.Add(new HashEntry(SlidingExpirationField, slidingExpiration.Value.Ticks));

            var redisTtl = CalculateExpiration(slidingExpiration, absoluteExpiration);

            // 使用 IBatch 将 HashSet 与 KeyExpire 合并为单次网络往返，减少延迟
            await ExecuteWithRetry(async () =>
            {
                var batch = _db.CreateBatch();
                var hashTask   = batch.HashSetAsync(fullKey, [.. entries]);
                var expireTask = batch.KeyExpireAsync(fullKey, redisTtl);
                batch.Execute();
                await Task.WhenAll(hashTask, expireTask).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "Redis 连接失败: {Key}", fullKey);
            throw new CacheUnavailableException("Redis 服务不可用", ex);
        }
        catch (Exception e) when (e is not OperationCanceledException and not CacheUnavailableException)
        {
            _logger.LogError(e, "序列化失败: {Key}", fullKey);
            throw new CacheSerializationException("对象序列化失败", e);
        }
    }


    /// 设置空值缓存
    private async Task SetNullValueAsync(string fullKey, TimeSpan expiration, CancellationToken ct)
    {
        await ExecuteWithRetry(() => _db.StringSetAsync(fullKey, NullValueMarker, expiration), ct).ConfigureAwait(false);
    }

    private async Task SetHashNullValueAsync(string fullKey, TimeSpan expiration, CancellationToken ct)
    {
        // 使用 IBatch 将 HashSet 与 KeyExpire 合并为单次网络往返
        await ExecuteWithRetry(async () =>
        {
            var batch = _db.CreateBatch();
            var hashTask   = batch.HashSetAsync(fullKey, [new HashEntry(ValueField, NullValueMarker)]);
            var expireTask = batch.KeyExpireAsync(fullKey, expiration);
            batch.Execute();
            await Task.WhenAll(hashTask, expireTask).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }



    /// 执行 Redis 操作并在连接失败时自动重试
    private static async Task<T> ExecuteWithRetry<T>(Func<Task<T>> operation, CancellationToken ct, int maxRetries = 2)
    {
        var retryCount = 0;
        while (true)
        {
            try
            {
                return await operation();
            }
            catch (RedisConnectionException) when (retryCount < maxRetries)
            {
                retryCount++;
                await Task.Delay(100 * retryCount, ct);
            }
        }
    }

    /// <summary>
    /// 以重试机制执行给定的操作，直到成功或达到最大重试次数。
    /// </summary>
    /// <param name="operation">要执行的异步操作。</param>
    /// <param name="ct">用于取消操作的令牌。</param>
    /// <param name="maxRetries">在抛出异常前允许的最大重试次数，默认值为2。</param>
    /// <returns>无返回值。</returns>
    private static async Task ExecuteWithRetry(Func<Task> operation, CancellationToken ct, int maxRetries = 2)
    {
        var retryCount = 0;
        while (true)
        {
            try
            {
                await operation();
                return;
            }
            catch (RedisConnectionException) when (retryCount < maxRetries)
            {
                retryCount++;
                await Task.Delay(100 * retryCount, ct);
            }
        }
    }


    /// <summary>
    /// 获取分布式锁，返回是否成功获得锁
    /// </summary>
    /// <param name="lockKey"></param>
    /// <param name="lockValue"></param>
    /// <param name="ct"></param>
    /// <param name="expiry"></param>
    /// <returns></returns>
    private async Task<bool> AcquireLockAsync(string lockKey, string lockValue, CancellationToken ct, TimeSpan? expiry = null)
    {
        // 这里不使用环境变量作为锁值，而是传入一个唯一值（比如 GUID），以支持同一进程内的重入锁和更安全的锁释放
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.LockTimeout);
        expiry ??= _options.DefaultLockExpiry;

        // 注意：StackExchange.Redis 的 LockTakeAsync 方法会自动处理锁的过期和安全释放，所以我们不需要在这里实现复杂的锁续期逻辑
        return await ExecuteWithRetry(() => _db.LockTakeAsync(lockKey, lockValue, expiry.Value), cts.Token).ConfigureAwait(false);
    }

    private async Task ReleaseLockAsync(string lockKey, string lockValue, CancellationToken ct)
    {
        // 释放锁时必须提供相同的 lockValue，确保只有持有锁的实例才能释放锁，防止误删他人锁
        await ExecuteWithRetry(() => _db.LockReleaseAsync(lockKey, lockValue), ct).ConfigureAwait(false);
    }


    /// <summary>
    /// 处理缓存命中时的逻辑，包括解析哈希条目中的值、绝对过期时间和滑动过期时间。
    /// </summary>
    /// <typeparam name="T">要反序列化的对象类型。</typeparam>
    /// <param name="hashEntries">包含缓存数据的哈希条目数组。</param>
    /// <param name="fullKey">完整的缓存键。</param>
    /// <param name="ct">用于取消操作的令牌。</param>
    /// <returns>反序列化后的对象或默认值（如果未找到有效值）。</returns>
    private async Task<T?> ProcessEnvelopeHitAsync<T>(HashEntry[] hashEntries, string fullKey, CancellationToken ct)
    {
        // 从抽屉里拿出各个字段
        var valueEntry = hashEntries.FirstOrDefault(x => x.Name == ValueField).Value;
        var absExpEntry = hashEntries.FirstOrDefault(x => x.Name == AbsoluteExpirationField).Value;
        var sldExpEntry = hashEntries.FirstOrDefault(x => x.Name == SlidingExpirationField).Value;

        if (!valueEntry.HasValue) return default;

        
        if (valueEntry == NullValueMarker)
        {
            _logger.LogDebug("空值穿透防御命中: {Key}", fullKey);
            return default;
        }

        if (absExpEntry.HasValue && absExpEntry.TryParse(out long absTicks))
        {
            var absTime = new DateTimeOffset(absTicks, TimeSpan.Zero);
            if (DateTimeOffset.UtcNow > absTime)
            {
                _logger.LogDebug("缓存已过绝对生存期，逻辑删除: {Key}", fullKey);
                await ExecuteWithRetry(() => _db.KeyDeleteAsync(fullKey), ct).ConfigureAwait(false);
                return default;
            }
        }

        // 命中时不再自动滑动续期，避免热路径额外 Expire 往返；需要续期时显式调用 RefreshAsync。
        _ = sldExpEntry;

        _cacheHits.Add(1);
        _logger.LogDebug("缓存命中: {Key}", fullKey);

        return SmartDeserialize<T>(valueEntry);
    }


    /// <summary>
    /// 将 Redis 值智能反序列化为目标类型。
    /// <para>
    /// 类型元数据通过 <see cref="TypeCache{T}"/> 静态缓存，避免每次反射开销。
    /// 值类型（对应 SetValueAsync 中 IsPrimitive || IsValueType 存储路径）从字符串解析；
    /// 引用类型从二进制字节反序列化，不产生额外字符串分配。
    /// </para>
    /// </summary>
    private T? SmartDeserialize<T>(RedisValue redisValue)
    {
        if (!redisValue.HasValue) return default;

        // 快速路径：string 类型无需任何类型解析
        if (TypeCache<T>.IsString)
            return (T)(object)redisValue.ToString();

        var underlyingType = TypeCache<T>.UnderlyingType;

        // 引用类型：以二进制存储，直接反序列化，避免 ToString() 分配
        if (!TypeCache<T>.IsStringRepresented)
            return _serializer.Deserialize<T>((byte[])redisValue!);

        // 值类型：以 ToString() 存储（对应 SetValueAsync 存储路径），从字符串还原
        var str = redisValue.ToString();

        if (underlyingType.IsEnum)
            return (T)Enum.Parse(underlyingType, str, ignoreCase: true);

        if (underlyingType == typeof(Guid))
            return (T)(object)Guid.Parse(str);

        if (underlyingType == typeof(DateTime))
            return (T)(object)DateTime.Parse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        if (underlyingType == typeof(DateTimeOffset))
            return (T)(object)DateTimeOffset.Parse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        if (underlyingType == typeof(TimeSpan))
            return (T)(object)TimeSpan.Parse(str, CultureInfo.InvariantCulture);

        return (T)Convert.ChangeType(str, underlyingType, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 按泛型参数 T 缓存常用类型元数据，CLR 保证每个 T 只初始化一次，零额外分配。
    /// </summary>
    private static class TypeCache<T>
    {
        /// <summary>去掉 Nullable 包装后的实际类型，对非 Nullable 类型等于 typeof(T)。</summary>
        public static readonly Type UnderlyingType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        /// <summary>是否为 string 类型（string 在 SetValueAsync 中走独立分支存储为 RedisValue 字符串）。</summary>
        public static readonly bool IsString = typeof(T) == typeof(string);

        /// <summary>
        /// 是否以字符串形式存入 Redis。
        /// 与 SetValueAsync 中 <c>IsPrimitive || IsValueType</c> 分支保持对称，
        /// 包括 int/bool/double 等基元类型、Guid、DateTime、DateTimeOffset、TimeSpan、decimal 等值类型。
        /// </summary>
        public static readonly bool IsStringRepresented =
            !IsString && (UnderlyingType.IsPrimitive || UnderlyingType.IsValueType);
    }


}
