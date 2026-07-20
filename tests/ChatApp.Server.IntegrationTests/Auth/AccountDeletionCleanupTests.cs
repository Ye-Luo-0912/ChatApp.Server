using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
using Core.Models.Friend;
using Core.Models.Identity;
using Core.Models.Moderation;
using Core.Models.Security;
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
        var (victim, peer, lifecycle) = await SeedScheduledUserAsync(db, "del");
        SeedRelated(db, victim.Id, peer.Id);
        await db.SaveChangesAsync();

        var processed = await lifecycle.ProcessDueDeletionsAsync();
        Assert.True(processed >= 1);

        Assert.False(await db.Users.AnyAsync(u => u.Id == victim.Id));
        Assert.False(await db.Friendships.AnyAsync(f => f.UserId == victim.Id || f.FriendId == victim.Id));
        Assert.False(await db.InAppNotifications.AnyAsync(n => n.UserId == victim.Id));
        Assert.False(await db.SecurityEvents.AnyAsync(e => e.UserId == victim.Id));
        Assert.False(await db.UserReports.AnyAsync(r => r.TargetUserId == victim.Id || r.ReporterId == victim.Id));
        Assert.False(await db.TrustedDevices.AnyAsync(d => d.UserId == victim.Id));
        Assert.True(await db.Users.AnyAsync(u => u.Id == peer.Id));
        Assert.True(await db.RealtimeOutbox.AnyAsync(o => o.PayloadJson!.Contains(victim.Id.ToString())));
    }

    [SkippableFact]
    public async Task CancelDeletion_AfterClaim_PreservesRelatedData()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var (victim, peer, lifecycle) = await SeedScheduledUserAsync(db, "cxl");
        SeedRelated(db, victim.Id, peer.Id);
        await db.SaveChangesAsync();

        lifecycle.AfterClaimHook = async (ids, ct) =>
        {
            Assert.Contains(victim.Id, ids);
            // 模拟用户在 Worker 领取租约后、清理前取消
            var cancelDb = postgres.CreateContext();
            var cancelSvc = new AccountLifecycleService(
                cancelDb,
                CreateTokenService(),
                new SecurityEventStore(cancelDb, NullLogger<SecurityEventStore>.Instance),
                NullLogger<AccountLifecycleService>.Instance);
            var cancel = await cancelSvc.CancelDeletionAsync(victim.Id, ct);
            Assert.True(cancel.Succeeded);
        };

        var processed = await lifecycle.ProcessDueDeletionsAsync();
        Assert.Equal(0, processed);

        Assert.True(await db.Users.AsNoTracking().AnyAsync(u => u.Id == victim.Id));
        Assert.Null((await db.Users.AsNoTracking().FirstAsync(u => u.Id == victim.Id)).DeletionScheduledAt);
        Assert.True(await db.Friendships.AsNoTracking().AnyAsync(f => f.UserId == victim.Id));
        Assert.True(await db.InAppNotifications.AsNoTracking().AnyAsync(n => n.UserId == victim.Id));
        Assert.True(await db.SecurityEvents.AsNoTracking().AnyAsync(e => e.UserId == victim.Id));
        Assert.True(await db.TrustedDevices.AsNoTracking().AnyAsync(d => d.UserId == victim.Id));
    }

    [SkippableFact]
    public async Task TwoWorkers_OnlyOnePurges_SameUser()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var (victim, peer, _) = await SeedScheduledUserAsync(db, "race");
        SeedRelated(db, victim.Id, peer.Id);
        await db.SaveChangesAsync();

        await using var dbA = postgres.CreateContext();
        await using var dbB = postgres.CreateContext();
        var workerA = new AccountLifecycleService(
            dbA, CreateTokenService(), new SecurityEventStore(dbA, NullLogger<SecurityEventStore>.Instance),
            NullLogger<AccountLifecycleService>.Instance);
        var workerB = new AccountLifecycleService(
            dbB, CreateTokenService(), new SecurityEventStore(dbB, NullLogger<SecurityEventStore>.Instance),
            NullLogger<AccountLifecycleService>.Instance);

        var results = await Task.WhenAll(
            workerA.ProcessDueDeletionsAsync(),
            workerB.ProcessDueDeletionsAsync());

        Assert.Equal(1, results.Sum());
        Assert.False(await db.Users.AsNoTracking().AnyAsync(u => u.Id == victim.Id));
    }

    [SkippableFact]
    public async Task AdminAudit_IsAnonymized_NotDeleted()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var (victim, peer, lifecycle) = await SeedScheduledUserAsync(db, "aud");
        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            AdminUserId = peer.Id,
            TargetUserId = victim.Id,
            Action = "DisableUser",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.True(await lifecycle.ProcessDueDeletionsAsync() >= 1);

        var audit = await db.AdminAuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == "DisableUser" && a.AdminUserId == peer.Id);
        Assert.Null(audit.TargetUserId);
        Assert.Contains($"anonymized-user:{victim.Id}", audit.Detail ?? "", StringComparison.Ordinal);
    }

    private async Task<(ApplicationUser Victim, ApplicationUser Peer, AccountLifecycleService Lifecycle)>
        SeedScheduledUserAsync(UserDbContext db, string prefix)
    {
        var tsid = new TsidGeneratorService();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var hasher = new BcryptPasswordHasher();
        var victim = new ApplicationUser
        {
            Id = tsid.GenerateTsid(),
            UserName = $"{prefix}-{suffix}",
            NormalizedUserName = $"{prefix}-{suffix}".ToUpperInvariant(),
            Email = $"{prefix}-{suffix}@ex.com",
            NormalizedEmail = $"{prefix}-{suffix}@EX.COM",
            EmailConfirmed = true,
            PasswordHash = hasher.HashPassword("Passw0rd!"),
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
            DeletionScheduledAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        var peer = new ApplicationUser
        {
            Id = tsid.GenerateTsid(),
            UserName = $"{prefix}-p-{suffix}",
            NormalizedUserName = $"{prefix}-P-{suffix}".ToUpperInvariant(),
            Email = $"{prefix}-p-{suffix}@ex.com",
            NormalizedEmail = $"{prefix}-P-{suffix}@EX.COM",
            EmailConfirmed = true,
            PasswordHash = hasher.HashPassword("Passw0rd!"),
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
        };
        db.Users.AddRange(victim, peer);
        await db.SaveChangesAsync();

        var lifecycle = new AccountLifecycleService(
            db,
            CreateTokenService(),
            new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance),
            NullLogger<AccountLifecycleService>.Instance);
        return (victim, peer, lifecycle);
    }

    private static void SeedRelated(UserDbContext db, long victimId, long peerId)
    {
        var tsid = new TsidGeneratorService();
        db.Friendships.Add(new UserFriendEntry
        {
            FriendshipId = tsid.GenerateTsid(),
            UserId = victimId,
            FriendId = peerId,
        });
        db.InAppNotifications.Add(new InAppNotification
        {
            UserId = victimId,
            Type = "security",
            Title = "t",
            Body = "b",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SecurityEvents.Add(new SecurityEvent
        {
            UserId = victimId,
            EventType = SecurityEventType.LoginSuccess,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.UserReports.Add(new UserReport
        {
            ReporterId = peerId,
            TargetType = UserReportTargetType.User,
            TargetUserId = victimId,
            Reason = "spam",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.TrustedDevices.Add(new TrustedDevice
        {
            UserId = victimId,
            TokenHash = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                .ToLowerInvariant(),
            DeviceIdHint = "hint",
            Label = "test",
            TrustedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
    }

    private TokenService CreateTokenService()
        => new(
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
}
