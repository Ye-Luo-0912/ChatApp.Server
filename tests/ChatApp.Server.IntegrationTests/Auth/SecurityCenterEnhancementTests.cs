using ChatApp.Server.IntegrationTests.Support;
using Core.Caching;
using Core.Interfaces;
using Core.Models.Identity;
using Core.Models.Security;
using Core.Settings;
using Infrastructure.Data;
using Infrastructure.Services;
using Infrastructure.Services.Auth;
using Infrastructure.Services.Email;
using Infrastructure.Services.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OtpNet;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisPostgresCollection))]
public sealed class SecurityCenterEnhancementTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [SkippableFact]
    public async Task Totp_SameTimestep_CannotReplay()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var password = "Passw0rd!";
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = await SeedAsync(db, $"totp-{suffix}", password);
        var mfa = CreateMfa(db);

        var (sharedKey, _, _) = await mfa.BeginSetupAsync(user.Id, password);
        var code = new Totp(Base32Encoding.ToBytes(sharedKey)).ComputeTotp();
        Assert.True((await mfa.ConfirmSetupAsync(user.Id, code)).Succeeded);

        user = await db.Users.FirstAsync(u => u.Id == user.Id);
        var totp = new Totp(Base32Encoding.ToBytes(sharedKey)).ComputeTotp();
        Assert.True(await mfa.TryVerifyAndConsumeTotpForUserAsync(user, totp));
        Assert.False(await mfa.TryVerifyAndConsumeTotpForUserAsync(user, totp));
    }

    [SkippableFact]
    public async Task RejectSuspiciousLogin_DoesNotForcePasswordChange()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var password = "Passw0rd!";
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = await SeedAsync(db, $"rej-{suffix}", password);
        var deviceId = AuthTestFactories.StableDeviceId("rej-dev");

        db.SecurityEvents.Add(new SecurityEvent
        {
            UserId = user.Id,
            EventType = SecurityEventType.LoginNewDevice,
            DeviceId = deviceId,
            ClientIp = "1.2.3.4",
            Detail = "session=sess-abc",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var evtId = await db.SecurityEvents.AsNoTracking()
            .Where(e => e.UserId == user.Id)
            .Select(e => e.Id)
            .FirstAsync();

        var account = CreateAccount(db, deviceId);
        var result = await account.RejectSuspiciousLoginAsync(user.Id, evtId);
        Assert.True(result!.Succeeded);
        Assert.True(await db.SecurityEvents.AnyAsync(e =>
            e.UserId == user.Id && e.EventType == SecurityEventType.LoginRejected));

        user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.False(user.MustChangePassword);
    }

    private MfaService CreateMfa(UserDbContext db)
    {
        var security = new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance);
        return new MfaService(
            db,
            AuthTestFactories.CreatePasswordHasher(),
            CreateRecoveryHasher(),
            CreateMfaProtector(),
            security,
            redis.Cache,
            NullLogger<MfaService>.Instance);
    }

    private UserAccountService CreateAccount(UserDbContext db, string deviceId)
    {
        var security = new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance);
        var tokens = new TokenService(
            redis.Cache,
            redis.Cache,
            redis.Cache,
            new FixedDeviceInfo(deviceId),
            Options.Create(new JwtSettings
            {
                AccessTokenExpirationMinutes = 30,
                RefreshTokenLength = 32,
                RefreshTokenExpirationDays = 3,
                Issuer = "ChatApp",
                Audience = "ChatApp",
                Secret = "test-mfa-jwt-secret-please-change",
            }),
            NullLogger<TokenService>.Instance);

        var trusted = new TrustedDeviceService(
            db, security, AuthTestFactories.CreatePasswordHasher(), CreateMfa(db), redis.Cache, redis.Cache,
            new FixedDeviceInfo(deviceId),
            new Microsoft.AspNetCore.Http.HttpContextAccessor(),
            Options.Create(new TrustedDeviceOptions()),
            NullLogger<TrustedDeviceService>.Instance);

        return new UserAccountService(
            new UserRepository(db, new TsidGeneratorService()),
            AuthTestFactories.CreatePasswordHasher(),
            new EmailVerificationService(new NoopEmail(), redis.Cache, redis.Cache),
            tokens,
            new FixedDeviceInfo(deviceId),
            new LocalAvatarStorage(
                Options.Create(new AvatarStorageOptions()),
                redis.Cache,
                redis.Cache,
                new AvatarReencodeQueue(
                    Options.Create(new AvatarStorageOptions()),
                    new AvatarReencodeMetrics()),
                NullLogger<LocalAvatarStorage>.Instance),
            security,
            new NoopNotify(),
            trusted,
            Options.Create(new ProfileOptions()),
            NullLogger<UserAccountService>.Instance);
    }

    private static IMfaSecretProtector CreateMfaProtector()
        => new AesGcmMfaSecretProtector(
            Options.Create(new SecurityOptions { SecretEncryptionKey = "test-mfa-encryption-key", KeyVersion = 1 }),
            Options.Create(new JwtSettings { Secret = "test-mfa-jwt-secret-please-change" }),
            new DummyHost(),
            NullLogger<AesGcmMfaSecretProtector>.Instance);

    private static IRecoveryCodeHasher CreateRecoveryHasher()
        => new HmacRecoveryCodeHasher(
            Options.Create(new SecurityOptions { SecretEncryptionKey = "test-mfa-encryption-key", KeyVersion = 1 }),
            Options.Create(new JwtSettings { Secret = "test-mfa-jwt-secret-please-change" }),
            new DummyHost(),
            AuthTestFactories.CreateCpuLimiter(),
            NullLogger<HmacRecoveryCodeHasher>.Instance);

    private static async Task<ApplicationUser> SeedAsync(UserDbContext db, string name, string password)
    {
        var hasher = AuthTestFactories.CreatePasswordHasher();
        var user = new ApplicationUser
        {
            Id = new TsidGeneratorService().GenerateTsid(),
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            Email = $"{name}@ex.com",
            NormalizedEmail = $"{name}@EX.COM".ToUpperInvariant(),
            EmailConfirmed = true,
            PasswordHash = await hasher.HashPasswordAsync(password),
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private sealed class DummyHost : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class NoopEmail : IEmailSender
    {
        public Task<Core.Models.Email.EmailResult> SendEmailAsync(
            string to, string subject, string body, bool isHtml = true, CancellationToken cancellation = default)
            => Task.FromResult(new Core.Models.Email.EmailResult { IsSuccess = true });

        public Task<Core.Models.Email.EmailResult> EnqueueEmailAsync(
            string to, string subject, string body, bool isHtml = true,
            string? emailType = null, string? idempotencyKey = null, CancellationToken cancellation = default)
            => SendEmailAsync(to, subject, body, isHtml, cancellation);

        public Task<Core.Models.Email.EmailResult> SendVerificationEmailAsync(
            string to, string username, string verificationToken, CancellationToken cancellation)
            => SendEmailAsync(to, "v", verificationToken, true, cancellation);
    }

    private sealed class NoopNotify : ISecurityNotificationService
    {
        public void StageNotify(long userId, string type, string title, string body, bool preferEmail) { }

        public Task NotifyAsync(long userId, string type, string title, string body, bool preferEmail,
            CancellationToken cancellationToken = default, string? idempotencyKey = null)
            => Task.CompletedTask;
    }
}
