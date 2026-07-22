using ChatApp.Server.IntegrationTests.Support;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Cache;

[Collection(nameof(RedisCollection))]
public sealed class StringPayloadConsumeTests(RedisTestFixture redis)
{
    [SkippableFact]
    public async Task TryGetAndDeleteStringPayload_Concurrent_OnlyOneWins()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var key = $"avatar:ticket:test-{Guid.NewGuid():N}";
        var payload = new Ticket(42, "avatars/42/x.bin", "image/jpeg", 100);
        await redis.Cache.SetStringPayloadAsync(key, payload, TimeSpan.FromMinutes(5));

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => redis.Cache.TryGetAndDeleteStringPayloadAsync<Ticket>(key))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r is not null));
        Assert.Equal(42, results.First(r => r is not null)!.UserId);
        Assert.Null(await redis.Cache.GetStringPayloadAsync<Ticket>(key));
    }

    private sealed record Ticket(long UserId, string ObjectKey, string ContentType, long ContentLength);
}
