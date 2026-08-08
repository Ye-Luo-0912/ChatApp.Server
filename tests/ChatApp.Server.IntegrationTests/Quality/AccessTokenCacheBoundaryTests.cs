using System.Text;
using System.Text.Json.Serialization;
using ChatApp.Auth.Contracts;
using ChatApp.Server.IntegrationTests.Support;
using Core.Caching;
using Core.Interfaces.Cache;
using Core.Models.Identity;
using Core.Models.Token;
using Core.Settings;
using Infrastructure.Serialization;
using Infrastructure.Services.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Quality;

public sealed class AccessTokenCacheBoundaryTests
{
    private const string ValidAccessToken = "AAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void DomainViewDoesNotDeclareRedisWirePropertyNames()
    {
        Assert.All(
            typeof(AccessTokenData).GetProperties(),
            property => Assert.Null(property.GetCustomAttributes(
                typeof(JsonPropertyNameAttribute),
                inherit: false).SingleOrDefault()));
    }

    [Fact]
    public async Task WriterUsesSharedKeyAndSharedRecordGoldenJson()
    {
        var cache = new RecordingCacheStore();
        var service = CreateTokenService(cache);
        var payload = new AccessTokenData
        {
            UserId = 42,
            UserName = "legacy-name",
            Roles = ["Admin", "User"],
            ExpiresAtMs = 1_735_689_600_123,
            SessionId = "session-42",
            DeviceIdHash = 123,
            SecurityVersion = 7,
            AccountState = AccountState.DeletionPending,
        };

        await service.StoreAccessTokenAsync(
            ValidAccessToken,
            payload,
            TimeSpan.FromMinutes(5));

        Assert.Equal(
            AccessTokenCacheKey.CreateLogical(ValidAccessToken),
            cache.LastSetKey);
        var record = Assert.IsType<AccessTokenCacheRecord>(cache.LastSetValue);
        Assert.Equal(AccessTokenAccountState.DeletionPending, record.AccountState);

        var json = Encoding.UTF8.GetString(new TextJsonSerializer().Serialize(record));
        Assert.Equal(
            "{\"u\":42,\"n\":\"legacy-name\",\"r\":[\"Admin\",\"User\"],\"e\":1735689600123,\"s\":\"session-42\",\"d\":123,\"v\":7,\"a\":1}",
            json);
    }

    [Fact]
    public async Task ReaderRequestsSharedRecordAndMapsLegacyFieldsToDomainModel()
    {
        var cache = new RecordingCacheStore();
        var service = CreateTokenService(cache);
        var key = AccessTokenCacheKey.CreateLogical(ValidAccessToken);
        cache.Seed(key, new AccessTokenCacheRecord
        {
            UserId = 84,
            UserName = "legacy-reader",
            Roles = ["Operator"],
            ExpiresAtMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            SessionId = "session-84",
            DeviceId = "legacy-device-id",
            DeviceIdHash = 456,
            SecurityVersion = 9,
            AccountState = AccessTokenAccountState.DeletionPending,
        });

        var result = await service.GetAccessTokenAsync(ValidAccessToken);

        Assert.Equal(typeof(AccessTokenCacheRecord), cache.LastReadType);
        Assert.Equal(key, cache.LastReadKey);
        Assert.NotNull(result);
        Assert.Equal(84, result.UserId);
        Assert.Equal("legacy-reader", result.UserName);
        Assert.Equal(["Operator"], Assert.IsType<string[]>(result.Roles));
        Assert.Equal("session-84", result.SessionId);
        Assert.Equal(456UL, result.DeviceIdHash);
        Assert.Equal(9, result.SecurityVersion);
        Assert.Equal(AccountState.DeletionPending, result.AccountState);
    }

    private static TokenService CreateTokenService(RecordingCacheStore cache) => new(
        cache,
        cache,
        cache,
        new FixedDeviceInfo("cache-boundary-device"),
        Options.Create(new JwtSettings
        {
            TokenL1CacheEnabled = false,
            RefreshTokenLength = 32,
        }),
        NullLogger<TokenService>.Instance);

    private sealed class RecordingCacheStore : ICacheValueStore, IAtomicCacheStore, ICacheSetStore
    {
        private string? _key;
        private object? _value;

        public bool IsHealthy => true;
        public string? LastSetKey { get; private set; }
        public object? LastSetValue { get; private set; }
        public string? LastReadKey { get; private set; }
        public Type? LastReadType { get; private set; }

        public void Seed(string key, object value)
        {
            _key = key;
            _value = value;
        }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            LastReadKey = key;
            LastReadType = typeof(T);
            return Task.FromResult(
                string.Equals(key, _key, StringComparison.Ordinal) && _value is T value
                    ? value
                    : default);
        }

        public Task SetAsync<T>(
            string key,
            T value,
            TimeSpan expiration,
            CancellationToken cancellationToken = default)
        {
            LastSetKey = key;
            LastSetValue = value;
            Seed(key, value!);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            if (string.Equals(key, _key, StringComparison.Ordinal))
            {
                _key = null;
                _value = null;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<T?>> GetManyAsync<T>(IReadOnlyList<string> keys, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<string?> StringGetAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task StringSetAsync(string key, string value, TimeSpan expiration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RemoveManyAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> StringSetIfNotExistsAsync(string key, string value, TimeSpan expiration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> TryStringCompareAndDeleteAsync(string key, string expectedValue, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> TryStringCompareAndExpireAsync(string key, string expectedValue, TimeSpan absoluteExpiration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> TryStringCompareAndSetAsync(string key, string expectedValue, string replacementValue, TimeSpan expiration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<long> StringIncrementAsync(string key, TimeSpan expirationWhenCreate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<T?> TryGetAndDeleteAsync<T>(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task SetManyAsync(IReadOnlyList<CacheSetRequest> writes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AtomicConsumeResult<TResult>> TryAtomicConsumeAsync<T, TResult>(string consumeKey, Func<T, AtomicConsumePlan<TResult>?> createPlan, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<long[]> EvaluateScriptAsync(string script, IReadOnlyList<string> keys, IReadOnlyList<string> args, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task SetAddAsync(string key, string member, TimeSpan? absoluteExpiration = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task SetRemoveAsync(string key, string member, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<string>> SetMembersAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task SetRemoveManyAsync(string key, IReadOnlyList<string> members, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
