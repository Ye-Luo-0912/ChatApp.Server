using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models.Friend;
using Core.Models.Identity;
using Core.Models.Moderation;
using Core.Models.Security;
using Core.Services;
using Infrastructure.Data;
using Infrastructure.Services;
using Infrastructure.Services.Auth;
using Infrastructure.Services.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisPostgresCollection))]
public sealed class AccountDeletionCleanupTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [SkippableFact]
    public async Task ProcessDueDeletions_CascadesRelatedData()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var tsid = new TsidGeneratorService();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var hasher = new BcryptPasswordHasher();

        var victim = new ApplicationUser
        {
            Id = tsid.GenerateTsid(),
            UserName = $"del-{suffix}",
            NormalizedUserName = $"DEL-{suffix}".ToUpperInvariant(),
            Email = $"del-{suffix}@ex.com",
            NormalizedEmail = $"DEL-{suffix}@EX.COM",
            EmailConfirmed = true,
            PasswordHash = hasher.HashPassword("Passw0rd!"),
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
            DeletionScheduledAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        var peer = new ApplicationUser
        {
            Id = tsid.GenerateTsid(),
            UserName = $"peer-{suffix}",
            NormalizedUserName = $"PEER-{suffix}".ToUpperInvariant(),
            Email = $"peer-{suffix}@ex.com",
            NormalizedEmail = $"PEER-{suffix}@EX.COM",
            EmailConfirmed = true,
            PasswordHash = hasher.HashPassword("Passw0rd!"),
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
        };
        db.Users.AddRange(victim, peer);
        await db.SaveChangesAsync();

        db.Friendships.Add(new UserFriendEntry
        {
            FriendshipId = tsid.GenerateTsid(),
            UserId = victim.Id,
            FriendId = peer.Id,
        });
        db.InAppNotifications.Add(new InAppNotification
        {
            UserId = victim.Id,
            Type = "security",
            Title = "t",
            Body = "b",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SecurityEvents.Add(new SecurityEvent
        {
            UserId = victim.Id,
            EventType = SecurityEventType.LoginSuccess,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.UserReports.Add(new UserReport
        {
            ReporterId = peer.Id,
            TargetType = UserReportTargetType.User,
            TargetUserId = victim.Id,
            Reason = "spam",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.TrustedDevices.Add(new TrustedDevice
        {
            UserId = victim.Id,
            TokenHash = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            DeviceIdHint = "hint",
            Label = "test",
            TrustedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await db.SaveChangesAsync();

        var tokens = new TokenService(
            redis.Cache,
            new FixedDeviceInfo("del-device"),
            Options.Create(new Core.Settings.JwtSettings
            {
                AccessTokenExpirationMinutes = 30,
                RefreshTokenLength = 32,
                RefreshTokenExpirationDays = 3,
                Issuer = "ChatApp",
                Audience = "ChatApp",
                Secret = "test-deletion-jwt-secret-please-change",
            }),
            NullLogger<TokenService>.Instance);

        var lifecycle = new AccountLifecycleService(
            db,
            tokens,
            new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance),
            NullLogger<AccountLifecycleService>.Instance);

        var processed = await lifecycle.ProcessDueDeletionsAsync();
        Assert.True(processed >= 1);

        Assert.False(await db.Users.AnyAsync(u => u.Id == victim.Id));
        Assert.False(await db.Friendships.AnyAsync(f => f.UserId == victim.Id || f.FriendId == victim.Id));
        Assert.False(await db.InAppNotifications.AnyAsync(n => n.UserId == victim.Id));
        Assert.False(await db.SecurityEvents.AnyAsync(e => e.UserId == victim.Id));
        Assert.False(await db.UserReports.AnyAsync(r => r.TargetUserId == victim.Id || r.ReporterId == victim.Id));
        Assert.False(await db.TrustedDevices.AnyAsync(d => d.UserId == victim.Id));
        Assert.True(await db.Users.AnyAsync(u => u.Id == peer.Id));
    }
}
