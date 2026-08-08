using System.Net;
using ChatApp.Server.IntegrationTests.Support;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisPostgresCollection))]
public sealed class LoginRiskOutboxTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [SkippableFact]
    public async Task SuccessfulLogin_PersistsRiskSignalForWorkerRecovery()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var factory = new ChatAppWebApplicationFactory(
            postgres.ConnectionString,
            redis.ConnectionString);
        using var client = factory.CreateClientWithDevice($"risk-{Guid.NewGuid():N}"[..24]);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var db = postgres.CreateContext())
            await WafTestHelpers.SeedUserAsync(
                db,
                $"risk-{suffix}",
                $"risk-{suffix}@example.com",
                "Passw0rd!");

        var login = await WafTestHelpers.LoginAsync(client, $"risk-{suffix}", "Passw0rd!");
        Assert.NotNull(login.UserId);

        await using var verify = postgres.CreateContext();
        var row = await verify.LoginRiskOutbox.AsNoTracking()
            .Where(x => x.UserId == login.UserId)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync();
        Assert.NotNull(row);
        Assert.Equal(Core.Models.Security.LoginRiskOutboxStatus.Pending, row!.Status);
        Assert.False(string.IsNullOrWhiteSpace(row.SessionId));
    }
}
