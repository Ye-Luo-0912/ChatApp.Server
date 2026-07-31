using Core.Caching;
using Core.Exceptions;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Infrastructure.Caching;

/// <summary>
/// 一次性状态存储实现：上传票、下载票、验证码、Step-up、MFA 挑战。
/// <para>故障策略：Garnet 不可用时失败关闭（fail-closed），抛出 <see cref="Core.Exceptions.CacheUnavailableException"/>。</para>
/// <para>恢复不能延长原始截止时间：TTL = expiresAt - 当前时间。</para>
/// </summary>
public sealed class GarnetOneTimeStateStore : IOneTimeStateStore
{
    private const string CompareDeleteScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly ISerializer _serializer;
    private readonly ILogger<GarnetOneTimeStateStore> _logger;
    private readonly string _keyPrefix;

    public GarnetOneTimeStateStore(
        IConnectionMultiplexer redis,
        ISerializer serializer,
        IOptions<RedisCacheOptions> options,
        ILogger<GarnetOneTimeStateStore> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _keyPrefix = options?.Value?.KeyPrefix ?? string.Empty;
    }

    public async Task IssueAsync<T>(
        string key,
        T payload,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(payload);

        var ttl = expiresAt - DateTimeOffset.UtcNow;
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "截止时间已过");

        var fullKey = NormalizeKey(key);
        var bytes = _serializer.Serialize(payload);

        try
        {
            await _redis.GetDatabase()
                .StringSetAsync(fullKey, bytes, ttl)
                .ConfigureAwait(false);
        }
        catch (RedisConnectionException ex)
        {
            throw new Core.Exceptions.CacheUnavailableException("一次性状态签发失败：Garnet 不可用", ex);
        }
        catch (RedisTimeoutException ex)
        {
            throw new Core.Exceptions.CacheUnavailableException("一次性状态签发失败：Garnet 超时", ex);
        }
    }

    public async Task<T?> TryConsumeAsync<T>(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return default;

        var fullKey = NormalizeKey(key);

        try
        {
            var value = await _redis.GetDatabase()
                .StringGetDeleteAsync(fullKey)
                .ConfigureAwait(false);

            if (!value.HasValue)
                return default;

            var bytes = (byte[]?)value;
            if (bytes is not { Length: > 0 })
                return default;

            return _serializer.Deserialize<T>(bytes);
        }
        catch (RedisConnectionException ex)
        {
            throw new Core.Exceptions.CacheUnavailableException("一次性状态消费失败：Garnet 不可用", ex);
        }
        catch (RedisTimeoutException ex)
        {
            throw new Core.Exceptions.CacheUnavailableException("一次性状态消费失败：Garnet 超时", ex);
        }
    }

    public async Task RestoreAsync<T>(
        string key,
        T payload,
        DateTimeOffset originalExpiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(payload);

        var remaining = originalExpiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _logger.LogWarning("一次性状态恢复跳过：已超过原始截止时间 Key={Key}", key);
            return;
        }

        var fullKey = NormalizeKey(key);
        var bytes = _serializer.Serialize(payload);

        try
        {
            await _redis.GetDatabase()
                .StringSetAsync(fullKey, bytes, remaining)
                .ConfigureAwait(false);
        }
        catch (RedisConnectionException ex)
        {
            throw new Core.Exceptions.CacheUnavailableException("一次性状态恢复失败：Garnet 不可用", ex);
        }
        catch (RedisTimeoutException ex)
        {
            throw new Core.Exceptions.CacheUnavailableException("一次性状态恢复失败：Garnet 超时", ex);
        }
    }

    public async Task<bool> TryConsumeIfEqualAsync(
        string key,
        string expectedValue,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        var fullKey = NormalizeKey(key);

        try
        {
            var result = await _redis.GetDatabase()
                .ScriptEvaluateAsync(CompareDeleteScript, [fullKey], [expectedValue])
                .ConfigureAwait(false);
            return (long)result == 1;
        }
        catch (RedisConnectionException ex)
        {
            throw new Core.Exceptions.CacheUnavailableException("一次性状态条件消费失败：Garnet 不可用", ex);
        }
        catch (RedisTimeoutException ex)
        {
            throw new Core.Exceptions.CacheUnavailableException("一次性状态条件消费失败：Garnet 超时", ex);
        }
    }

    public async Task<string?> PeekAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        var fullKey = NormalizeKey(key);

        try
        {
            var value = await _redis.GetDatabase()
                .StringGetAsync(fullKey)
                .ConfigureAwait(false);
            return value.HasValue ? value.ToString() : null;
        }
        catch (RedisConnectionException ex)
        {
            throw new Core.Exceptions.CacheUnavailableException("一次性状态读取失败：Garnet 不可用", ex);
        }
        catch (RedisTimeoutException ex)
        {
            throw new Core.Exceptions.CacheUnavailableException("一次性状态读取失败：Garnet 超时", ex);
        }
    }

    private string NormalizeKey(string key) =>
        CacheKeyBuilder.WithPrefix(_keyPrefix, key);
}
