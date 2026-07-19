using System.ComponentModel;
using System.Diagnostics;
using ChatApp.Server.IntegrationTests.Support;
using StackExchange.Redis;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Caching;

[Trait("Category", "Garnet")]
public sealed class GarnetRestartRecoveryTests
{
    private static string ConnectionString =>
      Environment.GetEnvironmentVariable("CHATAPP_TEST_GARNET")
      ?? "127.0.0.1:6379,abortConnect=false";

    [SkippableFact]
    public async Task ValueSurvivesClientReconnect()
    {
        var key = $"it:garnet:reconnect:{Guid.NewGuid():N}";
        const string expected = "persist-after-reconnect";

        var options = ConfigurationOptions.Parse(ConnectionString, true);
        options.AbortOnConnectFail = true;
        options.ConnectTimeout = 3000;

        IConnectionMultiplexer? first = null;
        IConnectionMultiplexer? second = null;
        try
        {
            first = await ConnectionMultiplexer.ConnectAsync(options);
            var db = first.GetDatabase();
            await db.StringSetAsync(key, expected);

            second = await ConnectionMultiplexer.ConnectAsync(options);
            var actual = await second.GetDatabase().StringGetAsync(key);

            Assert.Equal(expected, actual);
        }
        catch (RedisConnectionException ex)
        {
            Skip.If(true, $"Garnet unavailable at {ConnectionString}: {ex.Message}");
        }
        finally
        {
            first?.Dispose();
            second?.Dispose();
            try
            {
                using var cleanup = ConnectionMultiplexer.Connect(options);
                cleanup.GetDatabase().KeyDelete(key);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    /// <summary>
    /// 需要本机 docker-compose 的 <c>chatapp_garnet</c> 容器，且启用 AOF/--recover。
    /// 设置 <c>CHATAPP_TEST_GARNET_DOCKER_RESTART=1</c> 才会执行；CI 可跳过。
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Docker")]
    public async Task ValueSurvivesGarnetContainerRestart()
    {
        Skip.If(
            !string.Equals(
                Environment.GetEnvironmentVariable("CHATAPP_TEST_GARNET_DOCKER_RESTART"),
                "1",
                StringComparison.Ordinal),
            "Set CHATAPP_TEST_GARNET_DOCKER_RESTART=1 to run docker restart recovery test.");

        var key = $"it:garnet:restart:{Guid.NewGuid():N}";
        const string expected = "persist-after-container-restart";

        var options = ConfigurationOptions.Parse(ConnectionString, true);
        options.AbortOnConnectFail = true;
        options.ConnectTimeout = 5000;

        IConnectionMultiplexer? writer = null;
        IConnectionMultiplexer? reader = null;
        try
        {
            writer = await ConnectionMultiplexer.ConnectAsync(options);
            await writer.GetDatabase().StringSetAsync(key, expected);
            writer.Dispose();
            writer = null;

            var restart = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "restart chatapp_garnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(restart);
            await restart.WaitForExitAsync();
            Assert.Equal(0, restart.ExitCode);

            await WaitForGarnetAsync(options, TimeSpan.FromSeconds(30));

            reader = await ConnectionMultiplexer.ConnectAsync(options);
            var actual = await reader.GetDatabase().StringGetAsync(key);

            Assert.Equal(expected, actual);
        }
        catch (RedisConnectionException ex)
        {
            Skip.If(true, $"Garnet unavailable at {ConnectionString}: {ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            Skip.If(true, $"Docker restart unavailable: {ex.Message}");
        }
        finally
        {
            writer?.Dispose();
            reader?.Dispose();
            try
            {
                using var cleanup = ConnectionMultiplexer.Connect(options);
                cleanup.GetDatabase().KeyDelete(key);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private static async Task WaitForGarnetAsync(ConfigurationOptions options, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var mux = ConnectionMultiplexer.Connect(options);
                if (mux.IsConnected)
                    return;
            }
            catch (RedisConnectionException)
            {
                // retry until timeout
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("Garnet did not become ready after container restart.");
    }
}
