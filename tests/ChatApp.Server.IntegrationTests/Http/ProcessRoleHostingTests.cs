using System.Net;
using ChatApp.Server.IntegrationTests.Support;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Http;

public sealed class ProcessRoleHostingTests
{
    [Fact]
    public async Task WorkerRole_ExposesHealthButNotApiRoutes()
    {
        await using var factory = new ChatAppWebApplicationFactory(
            postgresConnection: "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused",
            redisConnection: "127.0.0.1:1,abortConnect=false",
            extraConfig: new Dictionary<string, string?>
            {
                ["DatabasePool:Role"] = "Worker",
            });
        using var client = factory.CreateClient();

        var liveness = await client.GetAsync("/health/live");
        var dependencies = await client.GetAsync("/health/dependencies");
        var capabilities = await client.GetAsync("/health/capabilities");
        var apiProbe = await client.GetAsync("/api/__test/problem");

        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, dependencies.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, capabilities.StatusCode);
        Assert.True(apiProbe.StatusCode == HttpStatusCode.NotFound,
            $"Unexpected worker API-probe status {(int)apiProbe.StatusCode}: {await apiProbe.Content.ReadAsStringAsync()}");
    }
}
