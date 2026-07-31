using ChatApp.Server.IntegrationTests.Support;
using Core.Models.Identity;
using Core.Models.Token;
using Core.Settings;
using Infrastructure.Auth;
using Infrastructure.Services.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisCollection))]
public sealed class TokenFormatValidationTests(RedisTestFixture redis)
{
    [Fact]
    public async Task ExternalInvalidTokens_AreRejectedWithoutHashingOrThrowing()
    {
        var service = CreateTokenService();
        var user = new ApplicationUser { Id = 701, UserName = "token-format-user" };
        var oversized = new string('A', 512);
        var malformedAccess = new string('!', 22);

        Assert.Null(await service.GetAccessTokenAsync(oversized));
        Assert.Null(await service.GetAccessTokenAsync(malformedAccess));
        await service.RevokeAccessTokenAsync(oversized);

        Assert.False(await service.ValidateRefreshTokenAsync(user.Id.ToString(), oversized));
        Assert.Null(await service.GetRefreshTokenAsync(user.Id.ToString(), oversized));
        await service.RevokeRefreshTokenAsync(user.Id.ToString(), oversized);
        Assert.Null(await service.IssueRefreshTokensAsync(user.Id.ToString(), oversized, user, ["User"]));
    }

    [SkippableFact]
    public async Task RevokeAccessToken_EvictsOtherInstanceL1ViaRedisPubSub()
    {
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        using var instanceAConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var instanceBConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var busA = new AccessTokenL1InvalidationBus(
            instanceAConnection,
            NullLogger<AccessTokenL1InvalidationBus>.Instance);
        var busB = new AccessTokenL1InvalidationBus(
            instanceBConnection,
            NullLogger<AccessTokenL1InvalidationBus>.Instance);

        await busA.StartAsync(CancellationToken.None);
        await busB.StartAsync(CancellationToken.None);
        try
        {
            var instanceA = CreateTokenService(busA);
            var instanceB = CreateTokenService(busB);
            var token = instanceA.Generate();
            var payload = new AccessTokenData
            {
                UserId = 703,
                UserName = "l1-invalidation-user",
                ExpiresAtMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            };

            await instanceA.StoreAccessTokenAsync(token, payload, TimeSpan.FromMinutes(5));
            Assert.NotNull(await instanceB.GetAccessTokenAsync(token)); // fills B's L1

            await instanceA.RevokeAccessTokenAsync(token);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            AccessTokenData? afterRevoke;
            do
            {
                afterRevoke = await instanceB.GetAccessTokenAsync(token);
                if (afterRevoke is null)
                    break;

                await Task.Delay(25);
            } while (DateTimeOffset.UtcNow < deadline);

            Assert.Null(afterRevoke);
        }
        finally
        {
            await busA.StopAsync(CancellationToken.None);
            await busB.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task GeneratedTokens_RemainAcceptedByFormatValidation()
    {
        var service = CreateTokenService();
        var user = new ApplicationUser { Id = 702, UserName = "token-format-valid" };

        var issued = await service.IssueLoginTokensAsync(user, ["User"]);

        Assert.NotNull(await service.GetAccessTokenAsync(issued.AccessToken));
        Assert.True(await service.ValidateRefreshTokenAsync(user.Id.ToString(), issued.RefreshToken));
    }

    private TokenService CreateTokenService(AccessTokenL1InvalidationBus? bus = null)
        => new(
            redis.Cache,
            redis.Cache,
            redis.Cache,
            new FixedDeviceInfo("token-format-device-0001"),
            Options.Create(new JwtSettings
            {
                AccessTokenExpirationMinutes = 30,
                RefreshTokenLength = 32,
                RefreshTokenExpirationDays = 3,
            }),
            NullLogger<TokenService>.Instance,
            bus);
}
