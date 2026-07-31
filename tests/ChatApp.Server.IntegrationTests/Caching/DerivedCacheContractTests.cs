using ChatApp.Server.IntegrationTests.Support;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Caching;

[Collection(nameof(RedisPostgresCollection))]
public sealed class DerivedCacheContractTests(RedisTestFixture redis)
{
    [SkippableFact]
    public async Task SetMany_RoundTripsAllValues_AndCanceledReadStopsImmediately()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var suffix = Guid.NewGuid().ToString("N");
        var keys = Enumerable.Range(0, 4)
            .Select(index => $"derived-batch:{suffix}:{index}")
            .ToArray();
        var values = keys
            .Select((key, index) => KeyValuePair.Create(key, index + 1))
            .ToArray();

        try
        {
            await redis.DerivedCache.SetManyAsync(values, TimeSpan.FromMinutes(1));

            var loaded = await redis.DerivedCache.TryGetManyAsync<int>(keys);
            Assert.Equal(keys.Length, loaded.Count);
            for (var i = 0; i < loaded.Count; i++)
            {
                Assert.True(loaded[i].Found);
                Assert.Equal(i + 1, loaded[i].Value);
            }

            using var canceled = new CancellationTokenSource();
            await canceled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                redis.DerivedCache.TryGetManyAsync<int>(keys, canceled.Token));
        }
        finally
        {
            await redis.DerivedCache.RemoveManyAsync(keys, CancellationToken.None);
        }
    }
}
