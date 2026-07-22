using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Models.Identity;
using Core.Models.Security;
using Core.Settings;
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
public sealed class TrustedDeviceSecurityTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [SkippableFact]
    public async Task Trust_RequiresPassword_RejectsBareAccessTokenPath()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var password = "Passw0rd!";
        var user = await SeedAsync(db, password);
        var trusted = CreateTrusted(db);

        var denied = await trusted.TrustCurrentAsync(
            user.Id, "d1", "phone", "127.0.0.1",
            password: null, mfaCode: null, stepUpToken: null);
        Assert.False(denied.Result.Succeeded);
        Assert.Contains(denied.Result.Errors, e => e.Code == "StepUpRequired");

        var ok = await trusted.TrustCurrentAsync(
            user.Id, "d1", "phone", "127.0.0.1",
            password, mfaCode: null, stepUpToken: null);
        Assert.True(ok.Result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(ok.PlainToken));
    }

    [SkippableFact]
    public async Task ConcurrentRotate_OnlyOneSucceeds()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var password = "Passw0rd!";
        var user = await SeedAsync(db, password);
        var trusted = CreateTrusted(db);
        var (_, plain) = await trusted.TrustCurrentAsync(
            user.Id, "d", "x", "1.1.1.1", password, null, null);
        Assert.False(string.IsNullOrWhiteSpace(plain));

        async Task<(bool Ok, string? RotatedPlainToken)> RotateOnce()
        {
            await using var scopedDb = postgres.CreateContext();
            var scoped = CreateTrusted(scopedDb);
            return await scoped.ValidateAndRotateAsync(user.Id, plain!, rotate: true);
        }

        var tasks = Enumerable.Range(0, 8).Select(_ => RotateOnce()).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.Equal(7, results.Count(r => !r.Ok));

        var winner = results.Single(r => r.Ok).RotatedPlainToken;
        Assert.False(string.IsNullOrWhiteSpace(winner));
        Assert.True(await trusted.ValidateTokenAsync(user.Id, winner!));
        Assert.False(await trusted.ValidateTokenAsync(user.Id, plain!));
    }

    [SkippableFact]
    public async Task CrossUser_TokenRejected_AndRevokeExpires()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var password = "Passw0rd!";
        var a = await SeedAsync(db, password, "a");
        var b = await SeedAsync(db, password, "b");
        var trusted = CreateTrusted(db);

        var (_, plain) = await trusted.TrustCurrentAsync(a.Id, "d", null, null, password, null, null);
        Assert.False(await trusted.ValidateTokenAsync(b.Id, plain!));

        await trusted.RevokeAllAsync(a.Id);
        Assert.False(await trusted.ValidateTokenAsync(a.Id, plain!));

        // 过期
        var (_, plain2) = await trusted.TrustCurrentAsync(a.Id, "d2", null, null, password, null, null);
        await db.TrustedDevices
            .Where(d => d.UserId == a.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.False(await trusted.ValidateTokenAsync(a.Id, plain2!));
    }

    [SkippableFact]
    public async Task Acknowledge_ReturnsPlainToken_AfterStepUp()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var password = "Passw0rd!";
        var user = await SeedAsync(db, password);
        db.SecurityEvents.Add(new SecurityEvent
        {
            UserId = user.Id,
            EventType = SecurityEventType.LoginNewDevice,
            DeviceId = "dev-ack",
            ClientIp = "10.0.0.1",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var evtId = await db.SecurityEvents.Where(e => e.UserId == user.Id)
            .Select(e => e.Id).OrderByDescending(id => id).FirstAsync();

        var trusted = CreateTrusted(db);
        var (result, plain) = await trusted.AcknowledgeUnusualLoginAsync(
            user.Id, evtId, "dev-ack", "10.0.0.1", password, null, null);
        Assert.True(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(plain));
        Assert.True(await trusted.ValidateTokenAsync(user.Id, plain!));
    }

    [SkippableFact]
    public async Task StepUpToken_IsSingleUse()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var password = "Passw0rd!";
        var user = await SeedAsync(db, password);
        var trusted = CreateTrusted(db);

        var (su, token) = await trusted.CreateStepUpTokenAsync(user.Id, password, null);
        Assert.True(su.Succeeded);

        var first = await trusted.TrustCurrentAsync(
            user.Id, "d", null, null, null, null, token);
        Assert.True(first.Result.Succeeded);

        var second = await trusted.TrustCurrentAsync(
            user.Id, "d2", null, null, null, null, token);
        Assert.False(second.Result.Succeeded);
    }

    private TrustedDeviceService CreateTrusted(UserDbContext db)
    {
        var security = new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance);
        var hasher = new BcryptPasswordHasher();
        var mfa = new MfaService(
            db, hasher, CreateRecoveryHasher(), CreateMfaProtector(), security, NullLogger<MfaService>.Instance);
        return new TrustedDeviceService(
            db, security, hasher, mfa, redis.Cache, NullLogger<TrustedDeviceService>.Instance);
    }

    private static IMfaSecretProtector CreateMfaProtector()
        => new AesGcmMfaSecretProtector(
            Options.Create(new SecurityOptions { SecretEncryptionKey = "test-mfa-encryption-key", KeyVersion = 1 }),
            Options.Create(new JwtSettings { Secret = "test-mfa-jwt-secret-please-change" }),
            new TestHostEnvironment(),
            NullLogger<AesGcmMfaSecretProtector>.Instance);

    private static IRecoveryCodeHasher CreateRecoveryHasher()
        => new HmacRecoveryCodeHasher(
            Options.Create(new SecurityOptions { SecretEncryptionKey = "test-mfa-encryption-key", KeyVersion = 1 }),
            Options.Create(new JwtSettings { Secret = "test-mfa-jwt-secret-please-change" }),
            new TestHostEnvironment(),
            NullLogger<HmacRecoveryCodeHasher>.Instance);

    private static async Task<ApplicationUser> SeedAsync(
        UserDbContext db, string password, string? tag = null)
    {
        var suffix = (tag ?? "") + Guid.NewGuid().ToString("N")[..8];
        var hasher = new BcryptPasswordHasher();
        var user = new ApplicationUser
        {
            Id = new TsidGeneratorService().GenerateTsid(),
            UserName = $"td-{suffix}",
            NormalizedUserName = $"TD-{suffix}".ToUpperInvariant(),
            Email = $"td-{suffix}@ex.com",
            NormalizedEmail = $"TD-{suffix}@EX.COM",
            EmailConfirmed = true,
            PasswordHash = await hasher.HashPasswordAsync(password),
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
