using Core.Interfaces.Cache;
using Infrastructure.Caching;
using Infrastructure.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Support;

/// <summary>
/// 提供 Redis/Garnet：优先 <c>CHATAPP_TEST_GARNET</c>，否则启动 Testcontainers Redis。
/// </summary>
public sealed class RedisTestFixture : IAsyncLifetime, IDisposable
{
    private RedisContainer? _container;
    private IConnectionMultiplexer? _multiplexer;

    public bool IsAvailable { get; private set; }

    public string? SkipReason { get; private set; }

    public string KeyPrefix { get; private set; } = string.Empty;

    public RedisCacheStore Cache { get; private set; } = null!;

    public IDerivedCache DerivedCache { get; private set; } = null!;

    public IOneTimeStateStore OneTimeState { get; private set; } = null!;

    public string ConnectionString { get; private set; } = "127.0.0.1:6379,abortConnect=false";

    public async Task InitializeAsync()
    {
        KeyPrefix = $"it:{Guid.NewGuid():N}:";

        var envConnection = Environment.GetEnvironmentVariable("CHATAPP_TEST_GARNET");
        if (!string.IsNullOrWhiteSpace(envConnection))
        {
            if (await TryConnectAsync(envConnection))
            {
                ConnectionString = envConnection;
                IsAvailable = true;
                return;
            }

            SkipReason = "CHATAPP_TEST_GARNET is set but connection failed.";
            return;
        }

        try
        {
            _container = new RedisBuilder()
                .WithImage("redis:7.2")
                .Build();

            await _container.StartAsync();
            var cs = $"{_container.GetConnectionString()},abortConnect=false";
            if (await TryConnectAsync(cs))
            {
                ConnectionString = cs;
                IsAvailable = true;
                return;
            }

            SkipReason = "Testcontainers Redis started but connection failed.";
        }
        catch (Exception ex)
        {
            SkipReason = $"Redis unavailable: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        Dispose();
        if (_container is not null)
            await _container.DisposeAsync();
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

    private async Task<bool> TryConnectAsync(string connectionString)
    {
        try
        {
            var options = ConfigurationOptions.Parse(connectionString, true);
            options.AbortOnConnectFail = true;
            options.ConnectTimeout = 5000;
            // 与生产一致（InfrastructureExtensions: 1000ms），避免连接断开时命令挂起
            options.SyncTimeout = 1000;
            options.AsyncTimeout = 1000;
            options.ConnectRetry = 1;

            _multiplexer = await ConnectionMultiplexer.ConnectAsync(options);

            var cacheOptions = Options.Create(new RedisCacheOptions
            {
                KeyPrefix = KeyPrefix,
            });

            Cache = new RedisCacheStore(
                _multiplexer,
                new TextJsonSerializer(),
                NullLogger<RedisCacheStore>.Instance,
                cacheOptions);

            DerivedCache = new GarnetDerivedCache(
                _multiplexer,
                new TextJsonSerializer(),
                cacheOptions,
                NullLogger<GarnetDerivedCache>.Instance);

            OneTimeState = new GarnetOneTimeStateStore(
                _multiplexer,
                new TextJsonSerializer(),
                cacheOptions,
                NullLogger<GarnetOneTimeStateStore>.Instance);

            return true;
        }
        catch
        {
            _multiplexer?.Dispose();
            _multiplexer = null;
            return false;
        }
    }
}
