using System.Text.Json;
using Core.Caching;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Infrastructure.Caching;

/// <summary>
/// 派生缓存实现：好友关系、地理位置等可重建数据。
/// <para>故障策略：Garnet 不可用时视为未命中（fail-open），不传播异常。</para>
/// <para>反序列化失败时删除损坏键并视为未命中。</para>
/// </summary>
public sealed class GarnetDerivedCache : IDerivedCache
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ISerializer _serializer;
    private readonly ILogger<GarnetDerivedCache> _logger;
    private readonly string _keyPrefix;

    public GarnetDerivedCache(
        IConnectionMultiplexer redis,
        ISerializer serializer,
        IOptions<RedisCacheOptions> options,
        ILogger<GarnetDerivedCache> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _keyPrefix = options?.Value?.KeyPrefix ?? string.Empty;
    }

    public async Task<CacheLookup<T>> TryGetAsync<T>(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return CacheLookup<T>.Miss;

        var fullKey = CacheKeyBuilder.WithPrefix(_keyPrefix, key);
        try
        {
            var value = await _redis.GetDatabase()
                .StringGetAsync(fullKey)
                .ConfigureAwait(false);

            if (!value.HasValue)
                return CacheLookup<T>.Miss;

            var bytes = (byte[]?)value;
            if (bytes is not { Length: > 0 })
                return CacheLookup<T>.Miss;

            try
            {
                return CacheLookup<T>.Hit(_serializer.Deserialize<T>(bytes)!);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "派生缓存数据损坏，删除键 Key={Key}", key);
                // 删除损坏键，下次回源重建
                await _redis.GetDatabase().KeyDeleteAsync(fullKey).ConfigureAwait(false);
                return CacheLookup<T>.Miss;
            }
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogDebug(ex, "派生缓存连接失败，视为未命中 Key={Key}", key);
            return CacheLookup<T>.Miss;
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogDebug(ex, "派生缓存超时，视为未命中 Key={Key}", key);
            return CacheLookup<T>.Miss;
        }
    }

    public async Task<IReadOnlyList<CacheLookup<T>>> TryGetManyAsync<T>(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
            return [];

        var redisKeys = new RedisKey[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            redisKeys[i] = CacheKeyBuilder.WithPrefix(_keyPrefix, keys[i]);

        RedisValue[] values;
        try
        {
            values = await _redis.GetDatabase()
                .StringGetAsync(redisKeys)
                .ConfigureAwait(false);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogDebug(ex, "派生缓存批量读取连接失败，全部视为未命中 Count={Count}", keys.Count);
            var missAll = new CacheLookup<T>[keys.Count];
            Array.Fill(missAll, CacheLookup<T>.Miss);
            return missAll;
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogDebug(ex, "派生缓存批量读取超时，全部视为未命中 Count={Count}", keys.Count);
            var missAll = new CacheLookup<T>[keys.Count];
            Array.Fill(missAll, CacheLookup<T>.Miss);
            return missAll;
        }

        var result = new CacheLookup<T>[keys.Count];
        for (var i = 0; i < values.Length; i++)
        {
            if (!values[i].HasValue)
            {
                result[i] = CacheLookup<T>.Miss;
                continue;
            }

            var bytes = (byte[]?)values[i];
            if (bytes is not { Length: > 0 })
            {
                result[i] = CacheLookup<T>.Miss;
                continue;
            }

            try
            {
                result[i] = CacheLookup<T>.Hit(_serializer.Deserialize<T>(bytes)!);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "派生缓存批量读取数据损坏，删除键 Key={Key}", keys[i]);
                try
                {
                    await _redis.GetDatabase().KeyDeleteAsync(redisKeys[i]).ConfigureAwait(false);
                }
                catch
                {
                    // 删除损坏键失败不影响未命中结果
                }
                result[i] = CacheLookup<T>.Miss;
            }
        }
        return result;
    }

    public async Task SetAsync<T>(
        string key,
        T value,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key) || ttl <= TimeSpan.Zero)
            return;

        var fullKey = CacheKeyBuilder.WithPrefix(_keyPrefix, key);
        var payload = _serializer.Serialize(value);

        try
        {
            await _redis.GetDatabase()
                .StringSetAsync(fullKey, payload, ttl)
                .ConfigureAwait(false);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogDebug(ex, "派生缓存写入失败（连接），忽略 Key={Key}", key);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogDebug(ex, "派生缓存写入失败（超时），忽略 Key={Key}", key);
        }
    }

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
                redisKeys[i] = CacheKeyBuilder.WithPrefix(_keyPrefix, keys[i]);
        }

        try
        {
            await _redis.GetDatabase()
                .KeyDeleteAsync(redisKeys)
                .ConfigureAwait(false);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogDebug(ex, "派生缓存批量删除失败（连接），忽略 Keys={Count}", keys.Count);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogDebug(ex, "派生缓存批量删除失败（超时），忽略 Keys={Count}", keys.Count);
        }
    }
}
