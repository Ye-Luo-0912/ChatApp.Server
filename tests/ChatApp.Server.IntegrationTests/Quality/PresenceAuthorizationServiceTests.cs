using ChatApp.Server.IntegrationTests.Support;
using Core.Caching;
using Core.Interfaces.Cache;
using Core.Models.Friend;
using Core.Models.Identity;
using Core.Models.Presence;
using Core.Settings;
using Infrastructure.Data;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Quality;

public sealed class PresenceAuthorizationServiceTests
{
    [Fact]
    public async Task AuthorizeAsync_NoSharedMembership_DoesNotAuthorizeAnyTarget()
    {
        var options = new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new UserDbContext(options);
        db.Users.AddRange(
            new ApplicationUser { Id = 1, UserName = "watcher" },
            new ApplicationUser { Id = 2, UserName = "target1" },
            new ApplicationUser { Id = 3, UserName = "target2" });
        await db.SaveChangesAsync();

        using var realtime = new RealtimePostgresDataSource(
            Options.Create(new MessageEvidenceOptions()),
            Options.Create(new DataExportStorageOptions()),
            NullLogger<RealtimePostgresDataSource>.Instance);
        var service = new PresenceAuthorizationService(
            db,
            new NoopCacheProvider(),
            new NoopCacheProvider(),
            realtime,
            NullLogger<PresenceAuthorizationService>.Instance);

        // Relationship data has migrated to RealtimeServices; without shared
        // conversation membership no target should be authorized.
        var allowed = await service.AuthorizeAsync(1, [2, 3]);

        Assert.Empty(allowed);
    }

    [Fact]
    public async Task AuthorizeAsync_UsesMutualFriendshipAndBlockDenyBeforeRealtimeMembership()
    {
        var options = new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new UserDbContext(options);
        db.Users.AddRange(
            new ApplicationUser { Id = 1, UserName = "watcher" },
            new ApplicationUser { Id = 2, UserName = "mutual" },
            new ApplicationUser { Id = 3, UserName = "blocked" },
            new ApplicationUser { Id = 4, UserName = "one-sided" });
        db.Friendships.AddRange(
            new UserFriendEntry { UserId = 1, FriendId = 2 },
            new UserFriendEntry { UserId = 2, FriendId = 1 },
            new UserFriendEntry { UserId = 1, FriendId = 3 },
            new UserFriendEntry { UserId = 3, FriendId = 1 },
            new UserFriendEntry { UserId = 1, FriendId = 4 });
        db.BlockRecords.Add(new BlockRecord
        {
            BlockerId = 3,
            BlockedUserId = 1,
            BlockedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        using var cache = new MemoryPresenceCache();
        using var realtime = new RealtimePostgresDataSource(
            Options.Create(new MessageEvidenceOptions()),
            Options.Create(new DataExportStorageOptions()),
            NullLogger<RealtimePostgresDataSource>.Instance);
        var service = new PresenceAuthorizationService(
            db,
            cache,
            cache,
            realtime,
            NullLogger<PresenceAuthorizationService>.Instance);

        var allowed = await service.AuthorizeAsync(1, [2, 3, 4]);

        Assert.Contains(2, allowed);
        Assert.DoesNotContain(3, allowed);
        Assert.DoesNotContain(4, allowed);
    }

    [Fact]
    public async Task CacheHit_RequiresCurrentRelationshipAndMembershipEpochs()
    {
        var options = new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new UserDbContext(options);
        db.Users.AddRange(
            new ApplicationUser { Id = 1, UserName = "watcher" },
            new ApplicationUser { Id = 2, UserName = "target" });
        await db.SaveChangesAsync();

        using var cache = new MemoryPresenceCache();
        await PresenceAuthorizationCache.EnsureEpochAsync(
            cache, cache, PresenceAuthorizationCache.RelationshipEpochKey(1, 2), CancellationToken.None);
        await PresenceAuthorizationCache.EnsureEpochAsync(
            cache, cache, PresenceAuthorizationCache.MembershipEpochKey(1), CancellationToken.None);
        await PresenceAuthorizationCache.EnsureEpochAsync(
            cache, cache, PresenceAuthorizationCache.MembershipEpochKey(2), CancellationToken.None);
        await cache.SetAsync(
            PresenceAuthorizationCache.DecisionKey(1, 2),
            new PresenceAuthorizationProjection
            {
                Allowed = true,
                RelationshipEpoch = 1,
                WatcherMembershipEpoch = 1,
                TargetMembershipEpoch = 1,
                MembershipDependent = true,
            },
            PresenceAuthorizationCache.DecisionTtl);

        using var realtime = new RealtimePostgresDataSource(
            Options.Create(new MessageEvidenceOptions()),
            Options.Create(new DataExportStorageOptions()),
            NullLogger<RealtimePostgresDataSource>.Instance);
        var service = new PresenceAuthorizationService(
            db,
            cache,
            cache,
            realtime,
            NullLogger<PresenceAuthorizationService>.Instance);

        Assert.Contains(2, await service.AuthorizeAsync(1, [2]));

        await PresenceAuthorizationCache.AdvanceMembershipEpochAsync(cache, 2, CancellationToken.None);
        Assert.DoesNotContain(2, await service.AuthorizeAsync(1, [2]));

        await cache.SetAsync(
            PresenceAuthorizationCache.DecisionKey(1, 2),
            new PresenceAuthorizationProjection
            {
                Allowed = true,
                RelationshipEpoch = 1,
                WatcherMembershipEpoch = 1,
                TargetMembershipEpoch = 2,
                MembershipDependent = true,
            },
            PresenceAuthorizationCache.DecisionTtl);
        await PresenceAuthorizationCache.AdvanceRelationshipEpochAsync(cache, 1, 2, CancellationToken.None);
        Assert.DoesNotContain(2, await service.AuthorizeAsync(1, [2]));
    }

    private sealed class MemoryPresenceCache : ICacheValueStore, IAtomicCacheStore, IDisposable
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public bool IsHealthy => true;

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                return Task.FromResult(_values.TryGetValue(key, out var value) && value is T typed
                    ? (T?)typed
                    : default);
        }

        public Task<IReadOnlyList<T?>> GetManyAsync<T>(
            IReadOnlyList<string> keys,
            CancellationToken cancellationToken = default)
        {
            var result = new T?[keys.Count];
            lock (_gate)
            {
                for (var i = 0; i < keys.Count; i++)
                {
                    if (_values.TryGetValue(keys[i], out var value) && value is T typed)
                    {
                        result[i] = (T?)typed;
                    }
                    else if (typeof(T) == typeof(long)
                             && _strings.TryGetValue(keys[i], out var raw)
                             && long.TryParse(raw, out var epoch))
                    {
                        result[i] = (T?)(object)epoch;
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<T?>>(result);
        }

        public Task<string?> StringGetAsync(string key, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                return Task.FromResult(_strings.TryGetValue(key, out var value) ? value : null);
        }

        public Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _values[key] = value;
            return Task.CompletedTask;
        }

        public Task StringSetAsync(string key, string value, TimeSpan expiration, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _strings[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _values.Remove(key);
                _strings.Remove(key);
            }

            return Task.CompletedTask;
        }

        public Task RemoveManyAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                foreach (var key in keys)
                {
                    _values.Remove(key);
                    _strings.Remove(key);
                }
            }

            return Task.CompletedTask;
        }

        public Task<bool> StringSetIfNotExistsAsync(
            string key,
            string value,
            TimeSpan expiration,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_strings.ContainsKey(key))
                    return Task.FromResult(false);
                _strings[key] = value;
                return Task.FromResult(true);
            }
        }

        public Task<bool> TryStringCompareAndDeleteAsync(
            string key,
            string expectedValue,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> TryStringCompareAndExpireAsync(
            string key,
            string expectedValue,
            TimeSpan absoluteExpiration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> TryStringCompareAndSetAsync(
            string key,
            string expectedValue,
            string replacementValue,
            TimeSpan expiration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<long> StringIncrementAsync(
            string key,
            TimeSpan expirationWhenCreate,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var next = _strings.TryGetValue(key, out var raw) && long.TryParse(raw, out var current)
                    ? current + 1
                    : 1;
                _strings[key] = next.ToString();
                return Task.FromResult(next);
            }
        }

        public Task<T?> TryGetAndDeleteAsync<T>(string key, CancellationToken cancellationToken = default)
            => Task.FromResult<T?>(default);

        public Task SetManyAsync(IReadOnlyList<CacheSetRequest> writes, CancellationToken cancellationToken = default)
        {
            foreach (var write in writes)
                _values[write.Key] = write.Value;
            return Task.CompletedTask;
        }

        public Task<AtomicConsumeResult<TResult>> TryAtomicConsumeAsync<T, TResult>(
            string consumeKey,
            Func<T, AtomicConsumePlan<TResult>?> createPlan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AtomicConsumeResult<TResult>.Fail());

        public Task<long[]> EvaluateScriptAsync(
            string script,
            IReadOnlyList<string> keys,
            IReadOnlyList<string> args,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<long>());

        public void Dispose()
        {
        }
    }
}
