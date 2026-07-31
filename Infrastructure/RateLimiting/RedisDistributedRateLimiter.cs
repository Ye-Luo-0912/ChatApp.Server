using Core.Interfaces;
using Infrastructure.Caching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Infrastructure.RateLimiting;

/// <summary>基于 Redis 的单例分布式限流器：一次 Lua 原子判定全部维度。</summary>
public sealed class RedisDistributedRateLimiter : IDistributedRateLimiter
{
    private const string FixedWindowScript = """
        local limit = tonumber(ARGV[1])
        local window = tonumber(ARGV[2])
        local retry = 0

        for i = 1, #KEYS do
            local count = tonumber(redis.call('GET', KEYS[i]) or '0')
            if count >= limit then
                local ttl = redis.call('PTTL', KEYS[i])
                if ttl < 1 then
                    redis.call('PEXPIRE', KEYS[i], window)
                    ttl = window
                end
                if ttl > retry then
                    retry = ttl
                end
            end
        end

        if retry > 0 then
            return {0, retry}
        end

        for i = 1, #KEYS do
            local count = redis.call('INCR', KEYS[i])
            if count == 1 then
                redis.call('PEXPIRE', KEYS[i], window)
            end
        end

        return {1, 0}
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly string _keyPrefix;
    private readonly int _clusterShardCount;
    private readonly ILogger<RedisDistributedRateLimiter> _logger;

    public RedisDistributedRateLimiter(
        IConnectionMultiplexer redis,
        IOptions<RedisCacheOptions> options,
        ILogger<RedisDistributedRateLimiter> logger,
        IOptions<Core.Settings.RateLimitingOptions>? rateOptions = null)
    {
        _redis = redis;
        _keyPrefix = options.Value.KeyPrefix;
        _clusterShardCount = Math.Max(1, rateOptions?.Value.ClusterShardCount ?? 1);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RateLimitAcquireResult> TryAcquireAsync(
        string policyName, IReadOnlyList<string> partitionKeys,
        int permitLimit, TimeSpan window,
        bool failOpen, CancellationToken cancellationToken = default)
    {
        if (permitLimit <= 0)
            return new RateLimitAcquireResult(false, TimeSpan.FromSeconds(1));
        if (partitionKeys.Count == 0)
            return new RateLimitAcquireResult(true, null);

        cancellationToken.ThrowIfCancellationRequested();

        var keys = new RedisKey[partitionKeys.Count];
        var shard = SelectShard(policyName, partitionKeys);
        var clusterTag = _clusterShardCount <= 1
            ? "{" + policyName + "}:"
            : "{" + policyName + ":shard-" + shard.ToString("D3") + "}:";
        for (var i = 0; i < keys.Length; i++)
            keys[i] = _keyPrefix + "rl:fw:" + clusterTag + partitionKeys[i];

        var windowMs = Math.Max(1L, (long)Math.Ceiling(window.TotalMilliseconds));
        var db = _redis.GetDatabase();

        try
        {
            var result = await db.ScriptEvaluateAsync(
                    FixedWindowScript,
                    keys,
                    [(RedisValue)permitLimit, (RedisValue)windowMs])
                .ConfigureAwait(false);

            if (result.IsNull)
            {
                _logger.LogWarning(
                    "限流 Lua 返回 null Policy={Policy} DimensionCount={DimensionCount}",
                    policyName, partitionKeys.Count);
                return failOpen
                    ? new RateLimitAcquireResult(true, null)
                    : new RateLimitAcquireResult(false, TimeSpan.FromSeconds(1));
            }

            var arr = (RedisResult[]?)result;
            if (arr is null || arr.Length < 2)
            {
                _logger.LogWarning(
                    "限流 Lua 返回格式异常 Policy={Policy} DimensionCount={DimensionCount}",
                    policyName, partitionKeys.Count);
                return failOpen ? new RateLimitAcquireResult(true, null) : new RateLimitAcquireResult(false, TimeSpan.FromSeconds(1));
            }
            var allowed = (long)arr[0] == 1;
            var ttl = (long)arr[1];

            if (allowed)
                return new RateLimitAcquireResult(true, null);

            var retryAfter = ttl > 0
                ? TimeSpan.FromMilliseconds(ttl)
                : window;
            return new RateLimitAcquireResult(false, retryAfter);
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            _logger.LogWarning(
                ex,
                "限流 Redis 不可用 Policy={Policy} DimensionCount={DimensionCount} FailOpen={FailOpen}",
                policyName, partitionKeys.Count, failOpen);
            return failOpen
                ? new RateLimitAcquireResult(true, null)
                : new RateLimitAcquireResult(false, TimeSpan.FromSeconds(1));
        }
    }

    private int SelectShard(string policyName, IReadOnlyList<string> partitionKeys)
    {
        if (_clusterShardCount <= 1)
            return 0;

        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in policyName)
            {
                hash ^= c;
                hash *= 16777619;
            }

            foreach (var key in partitionKeys)
            {
                hash ^= 0x1f;
                hash *= 16777619;
                foreach (var c in key)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
            }

            return (int)(hash % (uint)_clusterShardCount);
        }
    }
}
