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
    private const string ClaimScript = """
        if redis.call('EXISTS', KEYS[2]) == 1 then
          return nil
        end
        local value = redis.call('GET', KEYS[1])
        if not value then
          return nil
        end
        redis.call('DEL', KEYS[1])
        redis.call('SET', KEYS[2], value, 'PX', ARGV[1])
        return value
        """;
    private const string RestoreClaimScript = """
        local value = redis.call('GET', KEYS[2])
        if not value then
          return 0
        end
        if redis.call('EXISTS', KEYS[1]) == 1 then
          return 0
        end
        redis.call('SET', KEYS[1], value, 'PX', ARGV[1])
        redis.call('DEL', KEYS[2])
        return 1
        """;
    private const string CompareDeleteScript = """
        local current = redis.call('GET', KEYS[1])
        if current == ARGV[1] or current == ARGV[2] then
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

    public async Task<OneTimeStateClaim<T>?> TryClaimAsync<T>(
        string key,
        string claimKey,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(claimKey);
        var ttl = expiresAt - DateTimeOffset.UtcNow;
        if (ttl <= TimeSpan.Zero)
            return null;

        try
        {
            var result = await _redis.GetDatabase()
                .ScriptEvaluateAsync(
                    ClaimScript,
                    [NormalizeKey(key), NormalizeKey(claimKey)],
                    [Math.Max(1, (long)ttl.TotalMilliseconds)])
                .ConfigureAwait(false);
            var bytes = (byte[]?)result;
            if (bytes is not { Length: > 0 })
                return null;

            var payload = _serializer.Deserialize<T>(bytes);
            return payload is null
                ? null
                : new OneTimeStateClaim<T>(claimKey, payload, expiresAt);
        }
        catch (RedisConnectionException ex)
        {
            throw new Core.Exceptions.CacheUnavailableException("一次性状态 Claim 失败：Garnet 不可用", ex);
        }
        catch (RedisTimeoutException ex)
        {
            throw new Core.Exceptions.CacheUnavailableException("一次性状态 Claim 失败：Garnet 超时", ex);
        }
    }

    public async Task<bool> CompleteClaimAsync(
        string claimKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(claimKey))
            return false;
        try
        {
            return await _redis.GetDatabase()
                .KeyDeleteAsync(NormalizeKey(claimKey))
                .ConfigureAwait(false);
        }
        catch (RedisConnectionException ex)
        {
            throw new Core.Exceptions.CacheUnavailableException("一次性状态 Claim 完成失败：Garnet 不可用", ex);
        }
        catch (RedisTimeoutException ex)
        {
            throw new Core.Exceptions.CacheUnavailableException("一次性状态 Claim 完成失败：Garnet 超时", ex);
        }
    }

    public async Task<bool> RestoreClaimAsync(
        string key,
        string claimKey,
        DateTimeOffset originalExpiresAt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(claimKey))
            return false;
        var ttl = originalExpiresAt - DateTimeOffset.UtcNow;
        if (ttl <= TimeSpan.Zero)
            return false;
        try
        {
            var result = await _redis.GetDatabase()
                .ScriptEvaluateAsync(
                    RestoreClaimScript,
                    [NormalizeKey(key), NormalizeKey(claimKey)],
                    [Math.Max(1, (long)ttl.TotalMilliseconds)])
                .ConfigureAwait(false);
            return (long)result == 1;
        }
        catch (RedisConnectionException ex)
        {
            throw new Core.Exceptions.CacheUnavailableException("一次性状态 Claim 恢复失败：Garnet 不可用", ex);
        }
        catch (RedisTimeoutException ex)
        {
            throw new Core.Exceptions.CacheUnavailableException("一次性状态 Claim 恢复失败：Garnet 超时", ex);
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
                // String payloads are normally stored through ISerializer and
                // therefore arrive as JSON strings ("123456"). Older callers
                // and a few operational tools may have written the same value
                // as a raw Redis string. Accept both representations while
                // keeping the comparison and deletion atomic.
                .ScriptEvaluateAsync(
                    CompareDeleteScript,
                    [fullKey],
                    [expectedValue, _serializer.Serialize(expectedValue)])
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
