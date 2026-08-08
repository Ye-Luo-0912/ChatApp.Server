using System.Net;
using System.Net.Http.Json;
using ChatApp.Contracts.Http.Auth;
using ChatApp.Server.IntegrationTests.Support;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Http;

[Collection(nameof(RedisPostgresCollection))]
public sealed class HttpContractEndpointTests(
    PostgresTestFixture postgres,
    RedisTestFixture redis)
{
    [SkippableFact]
    public async Task LoginAndRefresh_ReturnCompleteCanonicalTokenContracts()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var factory = new ChatAppWebApplicationFactory(
            postgres.ConnectionString,
            redis.ConnectionString);
        using var client = factory.CreateClientWithDevice($"http-contract-{Guid.NewGuid():N}"[..32]);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var username = $"contract-{suffix}";
        await using (var db = postgres.CreateContext())
        {
            await WafTestHelpers.SeedUserAsync(
                db,
                username,
                $"{username}@ex.com",
                "Passw0rd!");
        }

        using var loginHttp = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { Username = username, Password = "Passw0rd!" },
            WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, loginHttp.StatusCode);
        var login = await loginHttp.Content.ReadFromJsonAsync<LoginResponse>(WafTestHelpers.Json);
        Assert.NotNull(login);
        Assert.True(login.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(login.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(login.RefreshToken));
        Assert.False(string.IsNullOrWhiteSpace(login.DeviceCredential));
        Assert.True(login.AccessTokenExpiresAtUtc > DateTime.UtcNow);
        Assert.True(login.RefreshTokenExpiresAtUtc > login.AccessTokenExpiresAtUtc);

        client.DefaultRequestHeaders.Add("X-Device-Credential", login.DeviceCredential);
        using var refreshHttp = await client.PostAsJsonAsync(
            "/api/auth/refresh-token",
            new RefreshTokenRequest
            {
                UserId = login.UserId!.Value,
                RefreshToken = login.RefreshToken!,
            },
            WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, refreshHttp.StatusCode);
        var refresh = await refreshHttp.Content.ReadFromJsonAsync<RefreshTokenResponse>(WafTestHelpers.Json);
        Assert.NotNull(refresh);
        Assert.True(refresh.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(refresh.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(refresh.RefreshToken));
        Assert.False(string.IsNullOrWhiteSpace(refresh.DeviceCredential));
        Assert.True(refresh.AccessTokenExpiresAtUtc > DateTime.UtcNow);
        Assert.True(refresh.RefreshTokenExpiresAtUtc > refresh.AccessTokenExpiresAtUtc);
    }
}
