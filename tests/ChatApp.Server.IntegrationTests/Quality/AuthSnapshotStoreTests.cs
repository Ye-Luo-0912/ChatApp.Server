using ChatApp.Server.IntegrationTests.Support;
using Core.Models.Auth;
using Core.Models.Identity;
using Core.Models.Token;
using Core.Settings;
using Infrastructure.Auth;
using Infrastructure.Data;
using Infrastructure.Services.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Quality;

public sealed class AuthSnapshotStoreTests
{
    [Fact]
    public void LoginResult_ReportsDeletionPendingForLegacyActiveRows()
    {
        var user = new ApplicationUser
        {
            Id = 903,
            UserName = "pending-login",
            AccountState = AccountState.Active,
            DeletionScheduledAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };
        var endpoint = new ServerEndPoint
        {
            Host = "127.0.0.1",
            Name = "test",
            Port = 1,
        };

        var result = LoginResult.Success(
            user,
            previousLoginDate: null,
            sessionId: "session",
            deviceIdHash: null,
            accessToken: "access",
            accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(1),
            refreshToken: "refresh",
            refreshTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(5),
            ref endpoint);

        Assert.Equal(AccountState.DeletionPending, result.AccountState);
    }

    [Fact]
    public void FenceState_UsesRestrictedDeletionSession_AndFailsClosedForInvalidSnapshots()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new UserAuthSnapshot
        {
            UserId = 901,
            SecurityVersion = 4,
            AccountState = AccountState.Active,
            DeletionScheduledAt = now.AddMinutes(5),
            LockoutUntil = now.AddMinutes(-1),
            ExpiresAt = now.AddMinutes(1),
        };

        Assert.Equal(AccountState.DeletionPending, snapshot.EffectiveAccountState(now));
        Assert.True(snapshot.IsAllowedAt(now));
        Assert.Equal(snapshot.SecurityVersion, snapshot.RoleVersion);

        snapshot.AccountState = AccountState.DeletionPending;
        snapshot.DeletionScheduledAt = null;
        Assert.False(snapshot.IsAllowedAt(now));

        snapshot.AccountState = AccountState.Active;
        snapshot.DeletionScheduledAt = now.AddMinutes(5);
        snapshot.ExpiresAt = now.AddSeconds(-1);
        Assert.True(snapshot.IsExpiredAt(now));
        Assert.False(snapshot.IsAllowedAt(now));
    }

    [Fact]
    public void L1VersionFloor_RejectsDelayedOldWriter_AndAcceptsNewSnapshot()
    {
        using var l1 = new AuthSnapshotL1Cache(maxEntries: 16, ttlMilliseconds: 1_000);
        l1.Set(new UserAuthSnapshot { UserId = 902, SecurityVersion = 1 });

        l1.Evict(902, minimumSecurityVersion: 2);
        l1.Set(new UserAuthSnapshot { UserId = 902, SecurityVersion = 1 });
        Assert.False(l1.TryGet(902, out _));

        l1.Set(new UserAuthSnapshot { UserId = 902, SecurityVersion = 2 });
        Assert.True(l1.TryGet(902, out var current));
        Assert.Equal(2, current!.SecurityVersion);

        // A delayed request must not overwrite the newly installed version.
        l1.Set(new UserAuthSnapshot { UserId = 902, SecurityVersion = 1 });
        Assert.True(l1.TryGet(902, out current));
        Assert.Equal(2, current!.SecurityVersion);
    }

    [Fact]
    public async Task FenceRead_DoesNotRequireRoles_AndFullReadHydratesThem()
    {
        var options = new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new UserDbContext(options);
        db.Users.Add(new ApplicationUser
        {
            Id = 901,
            UserName = "auth-fence-test",
        });
        await db.SaveChangesAsync();

        using var l1 = new AuthSnapshotL1Cache(maxEntries: 16, ttlMilliseconds: 1_000);
        var store = new AuthSnapshotStore(
            db,
            new NoopCacheProvider(),
            l1,
            Options.Create(new JwtSettings { AuthFenceDistributedTtlSeconds = 1 }),
            NullLogger<AuthSnapshotStore>.Instance);

        var fence = await store.GetFenceAsync(901);
        Assert.NotNull(fence);
        Assert.True(fence!.ClaimsLoaded);
        Assert.Equal("auth-fence-test", fence.UserName);
        Assert.Empty(fence.Roles);

        var full = await store.GetAsync(901);
        Assert.NotNull(full);
        Assert.True(full!.RolesLoaded);
        Assert.Equal("auth-fence-test", full.UserName);
        Assert.Empty(full.Roles);
    }
}
