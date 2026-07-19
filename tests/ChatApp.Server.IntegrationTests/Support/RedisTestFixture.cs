using Core.Interfaces.Cache;
using Infrastructure.Caching;
using Infrastructure.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Support;

/// <summary>
/// 连接本机 Garnet/Redis，并为每次测试使用独立 KeyPrefix，避免互相污染。
/// </summary>
public sealed class RedisTestFixture : IAsyncLifetime, IDisposable
{
    private IConnectionMultiplexer? _multiplexer;

    public string KeyPrefix { get; private set; } = string.Empty;

    public ICacheProvider Cache { get; private set; } = null!;

    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("CHATAPP_TEST_GARNET")
        ?? "127.0.0.1:6379,abortConnect=false";

    public async Task InitializeAsync()
    {
        KeyPrefix = $"it:{Guid.NewGuid():N}:";

        var options = ConfigurationOptions.Parse(ConnectionString, true);
        options.AbortOnConnectFail = true;
        options.ConnectTimeout = 3000;

        _multiplexer = await ConnectionMultiplexer.ConnectAsync(options);

        var cacheOptions = Options.Create(new RedisCacheOptions
        {
            KeyPrefix = KeyPrefix,
            DefaultSlidingExpiration = TimeSpan.FromMinutes(30),
            ExpirationJitterPercent = 0,
            LockTimeout = TimeSpan.FromSeconds(5),
            DefaultLockExpiry = TimeSpan.FromSeconds(3),
        });

        Cache = new RedisCaching(
            _multiplexer,
            new TextJsonSerializer(),
            NullLogger<RedisCaching>.Instance,
            cacheOptions);
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_multiplexer is null)
            return;

        try
        {
            foreach (var server in _multiplexer.GetServers())
            {
                if (!server.IsConnected)
                    continue;

                foreach (var key in server.Keys(pattern: KeyPrefix + "*"))
                    _multiplexer.GetDatabase().KeyDelete(key);
            }
        }
        catch
        {
            // 清理失败不影响测试进程退出
        }

        _multiplexer.Dispose();
        _multiplexer = null;
    }
}
