using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
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
    private readonly IDatabase _db;
    private readonly IConnectionMultiplexer _redis;
    private readonly ISerializer _serializer;
    private readonly ILogger<RedisCaching> _logger;
    private readonly RedisCacheOptions _options;
    private readonly Counter<int> _cacheHits;
    private readonly Counter<int> _cacheMisses;
    private readonly Histogram<double> _operationDuration;

    private const string NullValueMarker = "__NULL__";
    private static readonly Meter Meter = new("Infrastructure.Caching");
    // 添加 ActivitySource
    private static readonly ActivitySource ActivitySource = new("Infrastructure.Caching.Redis");
    public bool IsHealthy => _redis.IsConnected;

    private string NormalizeKey(string key) => _options.KeyPrefix + key;

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

        using var activity = ActivitySource.StartActivity("Redis.Get");
        var stopwatch = Stopwatch.StartNew();
        var fullKey = NormalizeKey(key);

        activity?.AddTag("cache.full_key", fullKey);
        try
        {
            // 获取缓存值
            var redisValue = await ExecuteWithRetry(() => _db.HashGetAllAsync(fullKey), cancellationToken);

            if (redisValue.Length > 0)
            {
                return await ProcessEnvelopeHitAsync<T>(redisValue, fullKey, cancellationToken).ConfigureAwait(false);
            }
            // 处理缓存未命中
            _cacheMisses.Add(1);
            _logger.LogDebug("缓存未命中: {Key}", fullKey);
            if (valueFactory == null) return default;

            // 获取分布式锁，防止缓存击穿
            var lockKey = $"lock:{fullKey}";
            var lockValue = Guid.CreateVersion7().ToString("N");
            if (await AcquireLockAsync(lockKey, lockValue, cancellationToken))
            {
                try
                {
                    // 双重检查
                    var recheckValue = await ExecuteWithRetry(() => _db.HashGetAsync(fullKey,"value"), cancellationToken);
                    if (!recheckValue.IsNullOrEmpty)
                    {
                        if (recheckValue != NullValueMarker) 
                            return SmartDeserialize<T>(recheckValue);
                        
                        _logger.LogDebug("空值穿透防御命中（双重检查）: {Key}", fullKey);
                        return default;

                    }

                    // 生成实际值
                    var value = await valueFactory();
                    var expiration = CalculateExpiration(slidingExpiration, absoluteExpiration);

                    if (value is null)
                    {
                        // 空值缓存
                        await SetHashNullValueAsync(fullKey, expiration, cancellationToken);
                    }
                    else
                    {
                        await SetValueAsync(fullKey, value, slidingExpiration, absoluteExpiration, cancellationToken);
                    }
                    return value;
                }
                finally
                {
                    await ReleaseLockAsync(lockKey, lockValue,cancellationToken);
                }
            }
            else
            {
                // 等待锁释放后重试获取缓存
                for (var i = 0; i < 3; i++)
                {
                    await Task.Delay(50, cancellationToken);
                    var retryValue = await ExecuteWithRetry(() => _db.HashGetAsync(fullKey, "value"), cancellationToken);
                    
                    if (!retryValue.HasValue) 
                        continue;
                    
                    return retryValue == NullValueMarker ? default : _serializer.Deserialize<CacheEnvelope<T>>(retryValue).Value;
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
            var redisValue = await ExecuteWithRetry(() => _db.StringGetAsync(fullKey), cancellationToken);

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
            var lockKey = $"lock:{fullKey}";
            var lockValue = Guid.CreateVersion7().ToString("N");
            if (await AcquireLockAsync(lockKey,lockValue, cancellationToken))
            {
                try
                {
                    // 双重检查
                    var recheckValue = await ExecuteWithRetry(() => _db.StringGetAsync(fullKey), cancellationToken);
                    if (recheckValue.HasValue)
                    {
                        if(recheckValue == NullValueMarker)
                            return null;

                        return recheckValue.ToString();
                    }
                        

                    // 生成实际值
                    var value = await valueFactory();
                    var expiration = CalculateExpiration(null, absoluteExpiration);

                    if (value == null)
                    {
                        await SetNullValueAsync(fullKey, expiration, cancellationToken);
                    }
                    else
                    {
                        await _db.StringSetAsync(fullKey, value,  expiration);
                    }

                    return value;
                }
                finally
                {
                    await ReleaseLockAsync(lockKey, lockValue,cancellationToken);
                }
            }
            else
            {
                // 等待锁释放后重试获取缓存
                for (int i = 0; i < 3; i++)
                {
                    await Task.Delay(50, cancellationToken);
                    var retryValue = await ExecuteWithRetry(() => _db.StringGetAsync(fullKey), cancellationToken);
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
            await _db.StringSetAsync(fullKey, value, expiration).WaitAsync(cancellationToken);
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
            var deleted = await ExecuteWithRetry(() => _db.KeyDeleteAsync(fullKey), cancellationToken);
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
            var absExpValue = await ExecuteWithRetry(() => _db.HashGetAsync(fullKey, "absExp"), cancellationToken);

            var newTtl = slidingExpiration;

            // 
            if (absExpValue.HasValue && absExpValue.TryParse(out long absTicks))
            {
                var absoluteExpiration = new DateTimeOffset(absTicks, TimeSpan.Zero);
                var maxRemaining = absoluteExpiration - DateTimeOffset.UtcNow;

                if (maxRemaining <= TimeSpan.Zero)
                {
                    // 已经过了绝对过期点时，直接清掉这个键，避免继续续期。
                    await _db.KeyDeleteAsync(fullKey);
                    return;
                }

                // 最终 TTL 不能超过绝对过期剩余时间。
                if (newTtl > maxRemaining) newTtl = maxRemaining;
            }


            await ExecuteWithRetry(() => _db.KeyExpireAsync(fullKey, newTtl), cancellationToken);
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
                var val = (string)(object)data;
                entries.Add(new("value", val));

            }
            else if (typeof(T).IsPrimitive || typeof(T).IsValueType)
            {
                var val = data!.ToString()!;
                entries.Add(new("value", val));
            }
            else
            {
                entries.Add(new("value", _serializer.Serialize(data)));
            }

            if (absoluteExpiration.HasValue)
            {
                var absTicks = DateTimeOffset.UtcNow.Add(absoluteExpiration.Value).Ticks;
                entries.Add(new("absExp", absTicks));
            }
            if (slidingExpiration.HasValue)
            {
                var slidExp = slidingExpiration.Value.Ticks;
                entries.Add(new HashEntry("slidExp", slidExp));
            }

            
            await ExecuteWithRetry(()=> _db.HashSetAsync(fullKey, [.. entries]), ct);

            //
            var redisTtl = CalculateExpiration(slidingExpiration, absoluteExpiration);
            await ExecuteWithRetry(() => _db.KeyExpireAsync(fullKey, redisTtl), ct);


        }
        catch (Exception e)
        {

            _logger.LogError(e, "序列化失败: {Key}", fullKey);
            throw new CacheSerializationException("对象序列化失败", e);
        }
    }


    /// 设置空值缓存
    private async Task SetNullValueAsync(string fullKey, TimeSpan expiration, CancellationToken ct)
    {
        await ExecuteWithRetry(() => _db.StringSetAsync(fullKey, NullValueMarker, expiration), ct);
    }

    private async Task SetHashNullValueAsync(string fullkey, TimeSpan expiration, CancellationToken ct)
    {
        await ExecuteWithRetry(() => _db.HashSetAsync(fullkey, [new HashEntry("value", NullValueMarker) ]), ct);

        await ExecuteWithRetry(() => _db.KeyExpireAsync(fullkey, expiration), ct);
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
        expiry = expiry ?? _options.DefaultLockExpiry;

        // 注意：StackExchange.Redis 的 LockTakeAsync 方法会自动处理锁的过期和安全释放，所以我们不需要在这里实现复杂的锁续期逻辑
        return await ExecuteWithRetry(() => _db.LockTakeAsync(lockKey, lockValue, expiry.Value), cts.Token);
    }

    private async Task ReleaseLockAsync(string lockKey, string lockValue, CancellationToken ct)
    {
        // 释放锁时必须提供相同的 lockValue，确保只有持有锁的实例才能释放锁，防止误删他人锁
        await ExecuteWithRetry(() => _db.LockReleaseAsync(lockKey, lockValue), ct);
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
        var valueEntry = hashEntries.FirstOrDefault(x => x.Name == "value").Value;
        var absExpEntry = hashEntries.FirstOrDefault(x => x.Name == "absExp").Value;
        var sldExpEntry = hashEntries.FirstOrDefault(x => x.Name == "sldExp").Value;

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
                await ExecuteWithRetry(()=> _db.KeyDeleteAsync(fullKey), ct);
                return default;
            }
        }

        if (sldExpEntry.HasValue && sldExpEntry.TryParse(out long sldTicks))
        {
            var newTtl = TimeSpan.FromTicks(sldTicks);


            if (absExpEntry.HasValue && absExpEntry.TryParse(out long absTicksForTruncate))
            {
                var absTime = new DateTimeOffset(absTicksForTruncate, TimeSpan.Zero);
                var maxRemaining = absTime - DateTimeOffset.UtcNow;
                if (newTtl > maxRemaining) newTtl = maxRemaining;
            }

            if (newTtl > TimeSpan.Zero)
            {
                await ExecuteWithRetry(() => _db.KeyExpireAsync(fullKey, newTtl), ct);
            }
        }

        _cacheHits.Add(1);
        _logger.LogDebug("缓存命中: {Key}", fullKey);

        return SmartDeserialize<T>(valueEntry);
    }


    /// <summary>
    /// 将 Redis 值智能反序列化为目标类型
    /// </summary>
    /// <typeparam name="T">要反序列化的类型</typeparam>
    /// <param name="redisValue">Redis 中存储的值</param>
    /// <returns>反序列化后的对象，如果无法反序列化则返回默认值</returns>
    private T? SmartDeserialize<T>(RedisValue redisValue)
    {
        var value = redisValue.ToString();

        var type = typeof(T);

        if (type == typeof(string))
            return (T)(object)value;

        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        if (underlyingType.IsEnum)
            return (T)Enum.Parse(underlyingType, value, ignoreCase: true);

        if (underlyingType == typeof(Guid))
            return (T)(object)Guid.Parse(value);

        if (underlyingType == typeof(DateTime))
            return (T)(object)DateTime.Parse(value);

        if (underlyingType == typeof(DateTimeOffset))
            return (T)(object)DateTimeOffset.Parse(value);

        if (underlyingType.IsPrimitive || underlyingType == typeof(decimal))
            return (T)Convert.ChangeType(value, underlyingType);

        // 复杂对象
        return _serializer.Deserialize<T>(redisValue);
    }


}
