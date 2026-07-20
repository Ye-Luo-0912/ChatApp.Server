using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
using Core.Models.Identity;
using Core.Models.Security;
using Core.Services;
using Core.Settings;
using Infrastructure.Data;
using Infrastructure.Services;
using Infrastructure.Services.Auth;
using Infrastructure.Services.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OtpNet;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisPostgresCollection))]
public sealed class MfaAndNotMeTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [SkippableFact]
    public async Task Login_RequiresMfa_ThenVerifyTotp_IssuesTokens()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var password = "Passw0rd!";
        var user = await SeedUserAsync(db, $"mfa-{suffix}", $"mfa-{suffix}@ex.com", password);

        var hasher = new BcryptPasswordHasher();
        var protector = CreateMfaProtector();
        var mfa = new MfaService(db, hasher, protector, new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance), NullLogger<MfaService>.Instance);
        var (sharedKey, _, _) = await mfa.BeginSetupAsync(user.Id, password);
        var code = new Totp(Base32Encoding.ToBytes(sharedKey)).ComputeTotp();
        Assert.True((await mfa.ConfirmSetupAsync(user.Id, code)).Succeeded);

        var auth = CreateAuth(db);
        var challenge = await auth.LoginAsync(user.UserName!, password);
        Assert.False(challenge.IsSuccess);
        Assert.True(challenge.RequiresTwoFactor);
        Assert.False(string.IsNullOrWhiteSpace(challenge.MfaToken));

        var totp = new Totp(Base32Encoding.ToBytes(sharedKey)).ComputeTotp();
        var login = await auth.VerifyMfaAsync(challenge.MfaToken!, totp);
        Assert.True(login.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(login.AccessToken));

        var afterMfa = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.Equal(0, afterMfa.AccessFailedCount);
        Assert.Null(afterMfa.LockoutEnd);
    }

    [Fact]
    public void MfaSecretProtector_KeyRotation_DecryptsWithPreviousVersion()
    {
        var plaintext = "JBSWY3DPEHPK3PXP";
        var v1 = new AesGcmMfaSecretProtector(
            Options.Create(new SecurityOptions
            {
                SecretEncryptionKey = "rotation-key-version-one-32chars!!",
                KeyVersion = 1,
            }),
            Options.Create(new JwtSettings { Secret = "unused-jwt-secret-for-rotation-test" }),
            new TestHostEnvironment(),
            NullLogger<AesGcmMfaSecretProtector>.Instance);

        var sealedV1 = v1.Protect(plaintext);
        Assert.StartsWith("v1:", sealedV1, StringComparison.Ordinal);

        var v2 = new AesGcmMfaSecretProtector(
            Options.Create(new SecurityOptions
            {
                SecretEncryptionKey = "rotation-key-version-two-32chars!!",
                KeyVersion = 2,
                PreviousSecretEncryptionKey = "rotation-key-version-one-32chars!!",
                PreviousKeyVersion = 1,
            }),
            Options.Create(new JwtSettings { Secret = "unused-jwt-secret-for-rotation-test" }),
            new TestHostEnvironment(),
            NullLogger<AesGcmMfaSecretProtector>.Instance);

        Assert.Equal(plaintext, v2.Unprotect(sealedV1));
        var sealedV2 = v2.Protect(plaintext);
        Assert.StartsWith("v2:", sealedV2, StringComparison.Ordinal);
        Assert.Equal(plaintext, v2.Unprotect(sealedV2));
    }

    [SkippableFact]
    public async Task ReportNotMe_RevokesSessions_AndBlocksLoginUntilPasswordChange()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var password = "Passw0rd!";
        var user = await SeedUserAsync(db, $"nm-{suffix}", $"nm-{suffix}@ex.com", password);

        var auth = CreateAuth(db);
        var login = await auth.LoginAsync(user.UserName!, password);
        Assert.True(login.IsSuccess);

        var evt = await db.SecurityEvents.AsNoTracking()
            .Where(e => e.UserId == user.Id && e.EventType == SecurityEventType.LoginSuccess)
            .OrderByDescending(e => e.Id)
            .FirstAsync();

        var (account, _, sessions) = CreateAccount(db);
        var result = await account.ReportNotMeAsync(user.Id, evt.Id);
        Assert.True(result!.Succeeded);

        var reloaded = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.True(reloaded.MustChangePassword);

        var blocked = await auth.LoginAsync(user.UserName!, password);
        Assert.False(blocked.IsSuccess);
        Assert.Equal(Core.Models.Token.LoginCheckStatus.NotAllowed, blocked.LoginCheckStatus);

        Assert.Empty(await sessions.ListSessionsAsync(user.Id.ToString()));
    }

    [SkippableFact]
    public async Task AdminAuditQuery_FiltersByAction()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            AdminUserId = 1,
            TargetUserId = 2,
            Action = "DisableUser",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            AdminUserId = 1,
            TargetUserId = 3,
            Action = "EnableUser",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var query = new AdminAuditQuery(db);
        var page = await query.QueryAsync(1, null, "DisableUser", null, null, null, 10);
        Assert.All(page.Items, i => Assert.Equal("DisableUser", i.Action));
        Assert.NotEmpty(page.Items);
    }

    private AuthService CreateAuth(UserDbContext db)
    {
        var tokens = CreateTokenService("mfa-device");
        return new AuthService(
            db,
            new BcryptPasswordHasher(),
            tokens,
            tokens,
            new FixedDeviceInfo("mfa-device"),
            new EmailVerificationService(new NoopEmail(), redis.Cache),
            new TsidGeneratorService(),
            new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance),
            new MfaService(db, new BcryptPasswordHasher(), CreateMfaProtector(), new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance), NullLogger<MfaService>.Instance),
            redis.Cache,
            new NoopNotify(),
            Options.Create(new RealtimeGatewayOptions { Host = "127.0.0.1", Port = 8888, Name = "test" }),
            NullLogger<AuthService>.Instance);
    }

    private (UserAccountService Account, IEmailVerificationService Email, TokenService Sessions)
        CreateAccount(UserDbContext db)
    {
        var hasher = new BcryptPasswordHasher();
        var repo = new UserRepository(db, new TsidGeneratorService());
        var email = new EmailVerificationService(new NoopEmail(), redis.Cache);
        var tokens = CreateTokenService("mfa-device");
        var account = new UserAccountService(
            repo, hasher, email, tokens, new FixedDeviceInfo("mfa-device"),
            new LocalAvatarStorage(
                Options.Create(new AvatarStorageOptions()),
                redis.Cache,
                new AvatarReencodeQueue(
                    Options.Create(new AvatarStorageOptions()),
                    new AvatarReencodeMetrics()),
                NullLogger<LocalAvatarStorage>.Instance),
            new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance),
            new NoopNotify(),
            Options.Create(new ProfileOptions()),
            NullLogger<UserAccountService>.Instance);
        return (account, email, tokens);
    }

    private TokenService CreateTokenService(string deviceId)
    {
        var jwt = Options.Create(new JwtSettings
        {
            AccessTokenExpirationMinutes = 30,
            RefreshTokenLength = 32,
            RefreshTokenExpirationDays = 3,
            Issuer = "ChatApp",
            Audience = "ChatApp",
            Secret = "test-mfa-jwt-secret-please-change",
        });
        return new TokenService(
            redis.Cache,
            new FixedDeviceInfo(deviceId),
            jwt,
            NullLogger<TokenService>.Instance);
    }

    private static IMfaSecretProtector CreateMfaProtector()
        => new AesGcmMfaSecretProtector(
            Options.Create(new SecurityOptions { SecretEncryptionKey = "test-mfa-encryption-key", KeyVersion = 1 }),
            Options.Create(new JwtSettings { Secret = "test-mfa-jwt-secret-please-change" }),
            new TestHostEnvironment(),
            NullLogger<AesGcmMfaSecretProtector>.Instance);

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static async Task<ApplicationUser> SeedUserAsync(
        UserDbContext db, string name, string email, string password)
    {
        var hasher = new BcryptPasswordHasher();
        var user = new ApplicationUser
        {
            Id = new TsidGeneratorService().GenerateTsid(),
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            PasswordHash = hasher.HashPassword(password),
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private sealed class NoopNotify : ISecurityNotificationService
    {
        public void StageNotify(long userId, string type, string title, string body, bool preferEmail) { }

        public Task NotifyAsync(long userId, string type, string title, string body, bool preferEmail,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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
}
