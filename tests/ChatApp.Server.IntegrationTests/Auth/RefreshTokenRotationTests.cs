using ChatApp.Server.IntegrationTests.Support;
using Core.Models.Identity;
using Core.Settings;
using Infrastructure.Services.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisCollection))]
public sealed class RefreshTokenRotationTests(RedisTestFixture redis)
{
    private const int ConcurrentRefreshCount = 100;

    [Fact]
    public async Task IssueRefreshTokensAsync_ConcurrentCalls_OnlyOneSucceeds()
    {
        const string deviceId = "device-integration-fixed";
        const long userId = 42;

        var tokenService = CreateTokenService(deviceId);
        var user = new ApplicationUser { Id = userId, UserName = "refresh-user" };
        IList<string> roles = ["User"];

        var login = await tokenService.IssueLoginTokensAsync(user, roles);
        Assert.False(string.IsNullOrWhiteSpace(login.RefreshToken));

        var oldRefreshToken = login.RefreshToken;
        var successes = 0;
        var failures = 0;
        string? winnerRefreshToken = null;

        var tasks = Enumerable.Range(0, ConcurrentRefreshCount).Select(async _ =>
        {
            var result = await tokenService.IssueRefreshTokensAsync(
                userId.ToString(), oldRefreshToken, user, roles);

            if (result is null)
            {
                Interlocked.Increment(ref failures);
                return;
            }

            Interlocked.Increment(ref successes);
            Interlocked.CompareExchange(ref winnerRefreshToken, result.Value.refreshToken, null);
        });

        await Task.WhenAll(tasks);

        Assert.Equal(1, successes);
        Assert.Equal(ConcurrentRefreshCount - 1, failures);
        Assert.False(string.IsNullOrEmpty(winnerRefreshToken));

        // 旧刷新令牌必须已被消费
        Assert.False(await tokenService.ValidateRefreshTokenAsync(userId.ToString(), oldRefreshToken));

        // 胜出的新刷新令牌可继续使用
        Assert.True(await tokenService.ValidateRefreshTokenAsync(userId.ToString(), winnerRefreshToken!));
    }

    [Fact]
    public async Task IssueRefreshTokensAsync_SecondSequentialCall_Fails()
    {
        const string deviceId = "device-sequential";
        const long userId = 7;

        var tokenService = CreateTokenService(deviceId);
        var user = new ApplicationUser { Id = userId, UserName = "seq-user" };
        IList<string> roles = ["User"];

        var login = await tokenService.IssueLoginTokensAsync(user, roles);

        var first = await tokenService.IssueRefreshTokensAsync(
            userId.ToString(), login.RefreshToken, user, roles);
        Assert.NotNull(first);

        var second = await tokenService.IssueRefreshTokensAsync(
            userId.ToString(), login.RefreshToken, user, roles);
        Assert.Null(second);
    }

    private TokenService CreateTokenService(string deviceId)
    {
        var jwt = Options.Create(new JwtSettings
        {
            AccessTokenExpirationMinutes = 30,
            RefreshTokenLength = 32,
            RefreshTokenExpirationDays = 3,
        });

        return new TokenService(
            redis.Cache,
            redis.Cache,
            redis.Cache,
            new FixedDeviceInfo(deviceId),
            jwt,
            NullLogger<TokenService>.Instance);
    }
}
