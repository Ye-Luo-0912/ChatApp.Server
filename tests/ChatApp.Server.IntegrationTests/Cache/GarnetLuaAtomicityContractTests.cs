using ChatApp.Server.IntegrationTests.Support;
using Core.Caching;
using Infrastructure.Caching;
using Infrastructure.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace ChatApp.Server.IntegrationTests.Cache;

/// <summary>
/// Garnet Lua 原子性并发契约测试。
/// </summary>
/// <remarks>
/// <para>
/// 验证所有 Lua 脚本在超高并发下严格原子，覆盖：
/// <list type="bullet">
///   <item><c>CompareDeleteScript</c>（compare-and-delete）</item>
///   <item><c>CompareSetScript</c>（compare-and-set）</item>
///   <item><c>CompareExpireScript</c>（compare-and-expire）</item>
///   <item><c>IncrementWithTtlScript</c>（INCR + 仅首次设置 TTL）</item>
///   <item><c>SetAddWithTtlScript</c>（SADD + 每次刷新 TTL）</item>
///   <item><c>FixedWindowScript</c>（多维固定窗口限流）</item>
///   <item><c>TryAtomicConsumeAsync</c>（MULTI/EXEC + WATCH CAS 事务）</item>
/// </list>
/// </para>
/// <para>
/// 这些测试可同时针对 Redis 7.2（CI 基线）与 Garnet 1.0.84（生产）运行。
/// 生产语义验证需连接真实 Garnet——即 docker-compose 的 <c>garnet_cache</c> 服务
/// （已启用 <c>--lua-transaction-mode</c>）。测试启动时会探测并记录服务端类型。
/// </para>
/// <para>
/// 关键背景：Garnet 的 <c>--lua-transaction-mode</c> 将 Lua 脚本以"锁定 Key、运行脚本、解锁"
/// 的事务方式执行；未启用时 Lua 不保证多键原子性。本套测试用于证明该模式下的原子性契约。
/// </para>
/// </remarks>
[Collection(nameof(RedisCollection))]
[Trait("Category", "Garnet")]
[Trait("Category", "GarnetContract")]
public sealed class GarnetLuaAtomicityContractTests(
    RedisTestFixture redis,
    ITestOutputHelper output) : IAsyncLifetime
{
    private string _serverIdentity = "unknown";

    public async Task InitializeAsync()
    {
        if (!redis.IsAvailable)
        {
            output.WriteLine("跳过：Redis/Garnet 不可用。");
            return;
        }

        _serverIdentity = await ProbeServerAsync();
        output.WriteLine("契约测试运行目标：{0}", _serverIdentity);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── compare-and-delete ───────────────────────────────────────────────

    [SkippableFact]
    public async Task CompareDelete_Concurrent_OnlyOneWins()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var key = $"contract:casdel:{Guid.NewGuid():N}";
        const string value = "v1";
        await redis.Cache.StringSetAsync(key, value, TimeSpan.FromMinutes(1));

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => redis.Cache.TryStringCompareAndDeleteAsync(key, value))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(49, results.Count(r => !r));
        Assert.Null(await redis.Cache.StringGetAsync(key));
    }

    [SkippableFact]
    public async Task CompareDelete_WrongExpectedValue_Fails()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var key = $"contract:casdel-wrong:{Guid.NewGuid():N}";
        await redis.Cache.StringSetAsync(key, "actual", TimeSpan.FromMinutes(1));

        var ok = await redis.Cache.TryStringCompareAndDeleteAsync(key, "stale");

        Assert.False(ok);
        Assert.Equal("actual", await redis.Cache.StringGetAsync(key));
    }

    // ── compare-and-set ──────────────────────────────────────────────────

    [SkippableFact]
    public async Task CompareSet_Concurrent_OnlyOneWins()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var key = $"contract:casset:{Guid.NewGuid():N}";
        const string original = "v1";
        await redis.Cache.StringSetAsync(key, original, TimeSpan.FromMinutes(1));

        var tasks = Enumerable.Range(0, 50)
            .Select(i => redis.Cache.TryStringCompareAndSetAsync(
                key, original, $"v2-{i}", TimeSpan.FromMinutes(1)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        // 只有一个 CAS 成功（将值改为 v2-N），其余因值已变而失败
        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(49, results.Count(r => !r));

        // 获胜者写入的新值存在，且不再等于 original
        var current = await redis.Cache.StringGetAsync(key);
        Assert.NotNull(current);
        Assert.NotEqual(original, current);
    }

    // ── compare-and-expire ───────────────────────────────────────────────

    [SkippableFact]
    public async Task CompareExpire_Concurrent_AllSucceedWhenValueUnchanged()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var key = $"contract:casexp:{Guid.NewGuid():N}";
        const string value = "v1";
        // 初始 TTL 较长，CAS expire 将缩短到 1 分钟
        await redis.Cache.StringSetAsync(key, value, TimeSpan.FromMinutes(10));

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => redis.Cache.TryStringCompareAndExpireAsync(
                key, value, TimeSpan.FromMinutes(1)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        // PEXPIRE 不改变值，所有 50 个 CAS 都应成功
        Assert.Equal(50, results.Count(r => r));

        // TTL 应被缩短到约 1 分钟（而非原来的 10 分钟）
        var ttl = await GetTtlAsync(key);
        Assert.True(ttl > TimeSpan.Zero);
        Assert.True(ttl <= TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(5),
            $"TTL 应缩短至约 1 分钟，实际 {ttl}");
    }

    [SkippableFact]
    public async Task CompareExpire_WrongExpectedValue_Fails()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var key = $"contract:casexp-wrong:{Guid.NewGuid():N}";
        await redis.Cache.StringSetAsync(key, "actual", TimeSpan.FromMinutes(10));

        var ok = await redis.Cache.TryStringCompareAndExpireAsync(
            key, "stale", TimeSpan.FromMinutes(1));

        Assert.False(ok);
        // TTL 不应被缩短
        var ttl = await GetTtlAsync(key);
        Assert.True(ttl > TimeSpan.FromMinutes(5));
    }

    // ── INCR + TTL ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task IncrementWithTtl_Concurrent_NoLostUpdates()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var key = $"contract:incr:{Guid.NewGuid():N}";
        const int concurrency = 100;
        var expiration = TimeSpan.FromSeconds(60);

        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => redis.Cache.StringIncrementAsync(key, expiration))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        // 无丢失更新：返回值应为 1..100 的排列
        var sorted = results.OrderBy(x => x).ToArray();
        for (var i = 0; i < concurrency; i++)
            Assert.Equal(i + 1, sorted[i]);

        // TTL 已设置（PEXPIRE 在首次创建时执行）
        var ttl = await GetTtlAsync(key);
        Assert.True(ttl > TimeSpan.Zero);
    }

    [SkippableFact]
    public async Task IncrementWithTtl_TtlSetOnlyOnFirstCreation()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var key = $"contract:incr-ttl:{Guid.NewGuid():N}";
        var expiration = TimeSpan.FromSeconds(60);

        // 首次 INCR：设置 TTL
        var first = await redis.Cache.StringIncrementAsync(key, expiration);
        Assert.Equal(1, first);

        var ttl1 = await GetTtlAsync(key);
        Assert.True(ttl1 > TimeSpan.Zero);
        Assert.True(ttl1 <= expiration);

        await Task.Delay(500);

        // 第二次 INCR：不应刷新 TTL
        var second = await redis.Cache.StringIncrementAsync(key, expiration);
        Assert.Equal(2, second);

        var ttl2 = await GetTtlAsync(key);
        Assert.True(ttl2 > TimeSpan.Zero);
        // TTL 应递减（未被刷新回 60s）
        Assert.True(ttl2 < ttl1,
            $"TTL 应递减而非刷新：ttl1={ttl1}, ttl2={ttl2}");
    }

    // ── SADD + TTL ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task SetAddWithTtl_Concurrent_AllMembersPresent()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var key = $"contract:sadd:{Guid.NewGuid():N}";
        var expiration = TimeSpan.FromSeconds(60);
        const int concurrency = 50;

        var tasks = Enumerable.Range(0, concurrency)
            .Select(i => redis.Cache.SetAddAsync(key, $"member-{i}", expiration))
            .ToArray();
        await Task.WhenAll(tasks);

        var members = await redis.Cache.SetMembersAsync(key);
        Assert.Equal(concurrency, members.Count);

        // TTL 已设置
        var ttl = await GetTtlAsync(key);
        Assert.True(ttl > TimeSpan.Zero);
    }

    [SkippableFact]
    public async Task SetAddWithTtl_AlwaysRefreshesTtl()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var key = $"contract:sadd-ttl:{Guid.NewGuid():N}";
        var expiration = TimeSpan.FromSeconds(60);

        await redis.Cache.SetAddAsync(key, "m1", expiration);

        await Task.Delay(500);

        // 第二次 SADD 前：TTL 应已递减
        var ttlBefore = await GetTtlAsync(key);
        Assert.True(ttlBefore < expiration - TimeSpan.FromMilliseconds(400),
            $"TTL 应已递减：ttlBefore={ttlBefore}");

        // 第二次 SADD 刷新 TTL（与 IncrementWithTtl 的"仅首次"语义不同）
        await redis.Cache.SetAddAsync(key, "m2", expiration);

        // 第二次 SADD 后：TTL 应回到接近原始值
        var ttlAfter = await GetTtlAsync(key);
        Assert.True(ttlAfter > ttlBefore,
            $"TTL 应被刷新回接近原始值：ttlBefore={ttlBefore}, ttlAfter={ttlAfter}");
    }

    // ── 多维固定窗口限流 ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task RateLimiter_Concurrent_ExactlyLimitAllowed()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var limiter = new RedisDistributedRateLimiter(
            connection,
            Options.Create(new RedisCacheOptions { KeyPrefix = redis.KeyPrefix }),
            NullLogger<RedisDistributedRateLimiter>.Instance);

        var policy = "contract-rl-" + Guid.NewGuid().ToString("N");
        var window = TimeSpan.FromMinutes(1);
        const int limit = 10;
        const int concurrency = 100;

        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => limiter.TryAcquireAsync(policy, ["dim:shared"], limit, window, failOpen: false))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(limit, results.Count(r => r.Allowed));
        Assert.Equal(concurrency - limit, results.Count(r => !r.Allowed));
    }

    [SkippableFact]
    public async Task RateLimiter_MultiDimension_RejectedPathDoesNotConsumeOtherDimensions()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var limiter = new RedisDistributedRateLimiter(
            connection,
            Options.Create(new RedisCacheOptions { KeyPrefix = redis.KeyPrefix }),
            NullLogger<RedisDistributedRateLimiter>.Instance);

        var policy = "contract-rlmd-" + Guid.NewGuid().ToString("N");
        var window = TimeSpan.FromMinutes(1);
        const int limit = 10;

        // 先耗尽维度 A（A 和 X 各计 limit 次）
        for (var i = 0; i < limit; i++)
        {
            var r = await limiter.TryAcquireAsync(policy, ["A", "X"], limit, window, failOpen: false);
            Assert.True(r.Allowed);
        }

        // A 已耗尽：并发混合请求
        //   even → ["A", "Y"]：A 超限，应全部拒绝，且不应消费 Y
        //   odd  → ["B", "Z"]：B、Z 全新，应全部放行
        const int concurrency = 20;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(i => limiter.TryAcquireAsync(
                policy,
                i % 2 == 0 ? ["A", "Y"] : ["B", "Z"],
                limit, window, failOpen: false))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        // even 全部拒绝（A 已达 limit）
        for (var i = 0; i < concurrency; i += 2)
            Assert.False(results[i].Allowed, $"带已耗尽维度 A 的请求 {i} 应被拒绝");

        // odd 全部放行（B、Z 各计 concurrency/2 = limit 次，未超限）
        for (var i = 1; i < concurrency; i += 2)
            Assert.True(results[i].Allowed, $"仅带独立维度的请求 {i} 应放行");

        // 关键契约：被拒绝的 even 请求不应消费 Y
        // Y 应为 0（odd 请求只消费 B 和 Z，不涉及 Y；even 请求在 pass 1 被拒，不进入 pass 2）
        var db = connection.GetDatabase();
        var yKey = redis.KeyPrefix + "rl:fw:{" + policy + "}:Y";
        var yCount = (long)await db.StringGetAsync(yKey);
        Assert.Equal(0, yCount);
    }

    // ── MULTI/EXEC + WATCH CAS 事务（TryAtomicConsumeAsync）─────────────

    [SkippableFact]
    public async Task AtomicConsume_Transaction_Concurrent_OnlyOneCommits()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var key = $"contract:consume:{Guid.NewGuid():N}";
        var payload = new ConsumePayload("original", 1);
        await redis.Cache.SetAsync(key, payload, TimeSpan.FromMinutes(1));

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => redis.Cache.TryAtomicConsumeAsync<ConsumePayload, bool>(
                key,
                _ => new AtomicConsumePlan<bool>
                {
                    Result = true,
                    AdditionalKeysToDelete = [],
                    Writes = [],
                }))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        // 只有一个事务提交成功，其余因 WATCH CAS 失败而回滚
        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(49, results.Count(r => !r.Succeeded));
        Assert.Null(await redis.Cache.GetAsync<ConsumePayload>(key));
    }

    [SkippableFact]
    public async Task AtomicConsume_Transaction_AdditionalKeysDeletedAtomically()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var consumeKey = $"contract:consume-main:{Guid.NewGuid():N}";
        var extraKey = $"contract:consume-extra:{Guid.NewGuid():N}";
        await redis.Cache.SetAsync(consumeKey, new ConsumePayload("v", 1), TimeSpan.FromMinutes(1));
        await redis.Cache.SetAsync(extraKey, "extra-value", TimeSpan.FromMinutes(1));

        var result = await redis.Cache.TryAtomicConsumeAsync<ConsumePayload, bool>(
            consumeKey,
            _ => new AtomicConsumePlan<bool>
            {
                Result = true,
                AdditionalKeysToDelete = [extraKey],
                Writes = [],
            });

        Assert.True(result.Succeeded);
        // 主键和附加键在同一事务内删除
        Assert.Null(await redis.Cache.GetAsync<ConsumePayload>(consumeKey));
        Assert.Null(await redis.Cache.StringGetAsync(extraKey));
    }

    [SkippableFact]
    public async Task AtomicConsume_Transaction_WritesAtomically()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var consumeKey = $"contract:consume-w-main:{Guid.NewGuid():N}";
        var writeKey = $"contract:consume-w-new:{Guid.NewGuid():N}";
        await redis.Cache.SetAsync(consumeKey, new ConsumePayload("v", 1), TimeSpan.FromMinutes(1));

        var result = await redis.Cache.TryAtomicConsumeAsync<ConsumePayload, string>(
            consumeKey,
            _ => new AtomicConsumePlan<string>
            {
                Result = "rotated",
                AdditionalKeysToDelete = [],
                Writes =
                [
                    new CacheSetRequest
                    {
                        Key = writeKey,
                        Value = new ConsumePayload("new", 2),
                        Expiration = TimeSpan.FromMinutes(1),
                    },
                ],
            });

        Assert.True(result.Succeeded);
        Assert.Equal("rotated", result.Value);
        // 主键已消费，新键已写入——在同一事务内
        Assert.Null(await redis.Cache.GetAsync<ConsumePayload>(consumeKey));
        var written = await redis.Cache.GetAsync<ConsumePayload>(writeKey);
        Assert.NotNull(written);
        Assert.Equal(2, written!.Version);
    }

    // ── 辅助方法 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 探测连接的服务端类型：通过 INFO server 区分 Garnet 与 Redis。
    /// Garnet 在 INFO 输出中包含 <c>garnet_version</c> 字段。
    /// </summary>
    private async Task<string> ProbeServerAsync()
    {
        try
        {
            using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
            var db = connection.GetDatabase();
            var info = (string?)await db.ExecuteAsync("INFO", "server");

            if (string.IsNullOrEmpty(info))
                return "unknown (INFO 返回空)";

            if (info.Contains("garnet_version", StringComparison.OrdinalIgnoreCase))
            {
                var garnetVer = ExtractField(info, "garnet_version") ?? "?";
                var redisVer = ExtractField(info, "redis_version") ?? "?";
                return $"Garnet (garnet_version={garnetVer}, redis_version={redisVer})";
            }

            var redisVersion = ExtractField(info, "redis_version") ?? "?";
            return $"Redis (redis_version={redisVersion})";
        }
        catch (Exception ex)
        {
            return $"unknown (探测失败: {ex.GetType().Name})";
        }
    }

    private static string? ExtractField(string info, string fieldName)
    {
        foreach (var line in info.Split('\n', '\r'))
        {
            if (line.StartsWith(fieldName + ":", StringComparison.OrdinalIgnoreCase))
                return line[(fieldName.Length + 1)..].Trim();
        }
        return null;
    }

    /// <summary>通过独立连接读取 key 的剩余 TTL（RedisCacheStore 未暴露此方法）。</summary>
    private async Task<TimeSpan?> GetTtlAsync(string key)
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var db = connection.GetDatabase();
        return await db.KeyTimeToLiveAsync(redis.KeyPrefix + key);
    }

    /// <summary>AtomicConsume 测试用的简单可序列化载荷。</summary>
    private sealed record ConsumePayload(string Value, int Version);
}
