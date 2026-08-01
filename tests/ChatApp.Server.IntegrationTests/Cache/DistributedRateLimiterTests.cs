using ChatApp.Server.IntegrationTests.Support;
using Core.Settings;
using Infrastructure.Caching;
using Infrastructure.RateLimiting;
using Infrastructure.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Cache;

[Collection(nameof(RedisCollection))]
public sealed class DistributedRateLimiterTests(RedisTestFixture redis)
{
    [Fact]
    public void RateLimitingOptions_RejectsMultipleClusterShards()
    {
        var result = new RateLimitingOptionsValidator().Validate(
            name: null,
            new RateLimitingOptions { ClusterShardCount = 16 });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("ClusterShardCount", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task RejectedMultiDimensionAcquire_DoesNotPartiallyConsumeOtherDimensions()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var limiter = new RedisDistributedRateLimiter(
            connection,
            Options.Create(new RedisCacheOptions { KeyPrefix = redis.KeyPrefix }),
            NullLogger<RedisDistributedRateLimiter>.Instance);
        var policy = "atomic-" + Guid.NewGuid().ToString("N");
        var window = TimeSpan.FromMinutes(1);

        var first = await limiter.TryAcquireAsync(
            policy, ["ip:a", "acct:x"], 1, window, failOpen: false);
        Assert.True(first.Allowed);

        var rejected = await limiter.TryAcquireAsync(
            policy, ["ip:a", "acct:y"], 1, window, failOpen: false);
        Assert.False(rejected.Allowed);

        var independent = await limiter.TryAcquireAsync(
            policy, ["ip:b", "acct:y"], 1, window, failOpen: false);
        Assert.True(independent.Allowed);
    }

    [SkippableFact]
    public async Task SameIpWithDifferentDevices_SharesIpPermitLimit()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var limiter = new RedisDistributedRateLimiter(
            connection,
            Options.Create(new RedisCacheOptions { KeyPrefix = redis.KeyPrefix }),
            NullLogger<RedisDistributedRateLimiter>.Instance,
            Options.Create(new RateLimitingOptions { ClusterShardCount = 1 }));
        var policy = "ip-device-" + Guid.NewGuid().ToString("N");
        var window = TimeSpan.FromMinutes(1);

        var first = await limiter.TryAcquireAsync(
            policy, ["ip:shared", "dev:one"], 1, window, failOpen: false);
        var changedDevice = await limiter.TryAcquireAsync(
            policy, ["ip:shared", "dev:two"], 1, window, failOpen: false);

        Assert.True(first.Allowed);
        Assert.False(changedDevice.Allowed);
    }
}
