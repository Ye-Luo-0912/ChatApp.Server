using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
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

        var hasher = AuthTestFactories.CreatePasswordHasher();
        var protector = CreateMfaProtector();
        var mfa = new MfaService(db, hasher, CreateRecoveryHasher(), protector, new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance), redis.Cache, NullLogger<MfaService>.Instance);
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

    [SkippableFact]
    public async Task Login_WithTrustedDeviceToken_SkipsMfa_AndRotatesToken()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var password = "Passw0rd!";
        var user = await SeedUserAsync(db, $"td-{suffix}", $"td-{suffix}@ex.com", password);

        var hasher = AuthTestFactories.CreatePasswordHasher();
        var mfa = new MfaService(db, hasher, CreateRecoveryHasher(), CreateMfaProtector(),
            new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance),
            redis.Cache, NullLogger<MfaService>.Instance);
        var (sharedKey, _, _) = await mfa.BeginSetupAsync(user.Id, password);
        var code = new Totp(Base32Encoding.ToBytes(sharedKey)).ComputeTotp();
        Assert.True((await mfa.ConfirmSetupAsync(user.Id, code)).Succeeded);

        var totp = new Totp(Base32Encoding.ToBytes(sharedKey)).ComputeTotp();
        var trusted = CreateTrusted(db);
        var (trustResult, plain) = await trusted.TrustCurrentAsync(
            user.Id, "hint", "laptop", "127.0.0.1", password, totp, null);
        Assert.True(trustResult.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(plain));

        var auth = CreateAuth(db);
        var without = await auth.LoginAsync(user.UserName!, password);
        Assert.True(without.RequiresTwoFactor);

        var withTrusted = await auth.LoginAsync(user.UserName!, password, plain);
        Assert.True(withTrusted.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(withTrusted.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(withTrusted.TrustedDeviceToken));
        Assert.NotEqual(plain, withTrusted.TrustedDeviceToken);

        // 旧令牌立即失效
        var reused = await auth.LoginAsync(user.UserName!, password, plain);
        Assert.True(reused.RequiresTwoFactor);

        // 轮换后的新令牌仍可用
        var withRotated = await auth.LoginAsync(user.UserName!, password, withTrusted.TrustedDeviceToken);
        Assert.True(withRotated.IsSuccess);
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

    [Fact]
    public async Task RecoveryCodeHasher_AcceptsLegacyBcryptDigests()
    {
        var hasher = CreateRecoveryHasher();
        const string plainDashed = "AABBCCDD-EEFF0011-22334455-66778899";
        const string plainNormalized = "AABBCCDDEEFF00112233445566778899";

        var bcryptDashed = BCrypt.Net.BCrypt.HashPassword(plainDashed, workFactor: 4);
        var bcryptNormalized = BCrypt.Net.BCrypt.HashPassword(plainNormalized, workFactor: 4);

        Assert.True(hasher.IsLegacyDigest(bcryptDashed));
        Assert.True(await hasher.VerifyAsync(plainDashed, bcryptDashed));
        Assert.True(await hasher.VerifyAsync(plainDashed.Replace("-", ""), bcryptDashed));
        Assert.True(await hasher.VerifyAsync(plainNormalized, bcryptNormalized));
        Assert.False(await hasher.VerifyAsync("wrong-code", bcryptDashed));

        // 新摘要仍为HMAC
        var hmac = hasher.Hash(plainDashed);
        Assert.StartsWith("v1:", hmac, StringComparison.Ordinal);
        Assert.True(await hasher.VerifyAsync(plainDashed, hmac));
        Assert.False(hasher.IsLegacyDigest(hmac));
    }

    [SkippableFact]
    public async Task RecoveryCode_LegacyBcrypt_ConsumeAndSignalsUpgrade()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var password = "Passw0rd!";
        var user = await SeedUserAsync(db, $"rcl-{suffix}", $"rcl-{suffix}@ex.com", password);

        var plain = "11223344-55667788-99AABBCC-DDEEFF00";
        var bcryptHash = BCrypt.Net.BCrypt.HashPassword(plain, workFactor: 4);
        var hmacOther = CreateRecoveryHasher().Hash("FFEEDDCC-BBAA9988-77665544-33221100");

        // 启用 MFA：手工写入含 BCrypt 的恢复码列表
        var protector = CreateMfaProtector();
        var key = OtpNet.KeyGeneration.GenerateRandomKey(20);
        var base32 = OtpNet.Base32Encoding.ToString(key);
        user.TotpSecret = protector.Protect(base32);
        user.TwoFactorEnabled = true;
        user.RecoveryCodesHashJson = System.Text.Json.JsonSerializer.Serialize(new[] { bcryptHash, hmacOther });
        await db.SaveChangesAsync();

        var hasher = AuthTestFactories.CreatePasswordHasher();
        var mfa = new MfaService(
            db, hasher, CreateRecoveryHasher(), protector,
            new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance),
            redis.Cache, NullLogger<MfaService>.Instance);

        Assert.True(await mfa.TryConsumeRecoveryCodeAsync(user.Id, plain));
        Assert.False(await mfa.TryConsumeRecoveryCodeAsync(user.Id, plain));

        var stored = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.Contains("v1:", stored.RecoveryCodesHashJson, StringComparison.Ordinal);
        Assert.DoesNotContain(bcryptHash, stored.RecoveryCodesHashJson, StringComparison.Ordinal);

        // 仍含另一枚HMAC；升级信号仅在仍有BCrypt 时触发——此处已消费掉BCrypt
        Assert.False(HmacRecoveryCodeHasher.ContainsLegacyDigestsStatic(stored.RecoveryCodesHashJson));

        // 重新种入旧BCrypt，走登录成功路径应带 RequiresRecoveryCodeRegeneration
        stored = await db.Users.FirstAsync(u => u.Id == user.Id);
        stored.RecoveryCodesHashJson = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            BCrypt.Net.BCrypt.HashPassword("AABBCCDD-EEFF0011-22334455-66778899", workFactor: 4),
        });
        await db.SaveChangesAsync();

        var auth = CreateAuth(db);
        var totp = new Totp(Base32Encoding.ToBytes(base32)).ComputeTotp();
        // 先密码登录拿 MFA token
        var step1 = await auth.LoginAsync(user.UserName!, password);
        Assert.True(step1.RequiresTwoFactor);
        Assert.False(string.IsNullOrWhiteSpace(step1.MfaToken));
        var step2 = await auth.VerifyMfaAsync(step1.MfaToken!, totp);
        Assert.True(step2.IsSuccess);
        Assert.True(step2.RequiresRecoveryCodeRegeneration);
        Assert.True(await db.SecurityEvents.AnyAsync(e =>
            e.UserId == user.Id && e.EventType == SecurityEventType.MfaRecoveryCodesUpgradeRequired));
    }

    [Fact]
    public async Task RecoveryCodeHasher_HmacVersioning_AndConsumeOnce()
    {
        var hasher = CreateRecoveryHasher();
        var plain = hasher.GeneratePlainCode();
        Assert.Contains('-', plain);
        var digest = hasher.Hash(plain);
        Assert.StartsWith("v1:", digest, StringComparison.Ordinal);
        Assert.True(await hasher.VerifyAsync(plain, digest));
        Assert.True(await hasher.VerifyAsync(plain.Replace("-", ""), digest));
        Assert.False(await hasher.VerifyAsync("wrong-code", digest));

        var rotated = new HmacRecoveryCodeHasher(
            Options.Create(new SecurityOptions
            {
                SecretEncryptionKey = "rotation-key-version-two-32chars!!",
                KeyVersion = 2,
                PreviousSecretEncryptionKey = "test-mfa-encryption-key",
                PreviousKeyVersion = 1,
            }),
            Options.Create(new JwtSettings { Secret = "unused-jwt-secret-for-rotation-test" }),
            new TestHostEnvironment(),
            AuthTestFactories.CreateCpuLimiter(),
            NullLogger<HmacRecoveryCodeHasher>.Instance);
        Assert.True(await rotated.VerifyAsync(plain, digest));
        var v2Digest = rotated.Hash(plain);
        Assert.StartsWith("v2:", v2Digest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoveryCodeHasher_LegacyBcrypt_RespectsSharedCpuGate()
    {
        var limiter = AuthTestFactories.CreateCpuLimiter(maxConcurrent: 1, acquireTimeoutMs: 30);
        var hasher = new HmacRecoveryCodeHasher(
            Options.Create(new SecurityOptions { SecretEncryptionKey = "test-mfa-encryption-key", KeyVersion = 1 }),
            Options.Create(new JwtSettings { Secret = "test-mfa-jwt-secret-please-change" }),
            new TestHostEnvironment(),
            limiter,
            NullLogger<HmacRecoveryCodeHasher>.Instance);

        const string plain = "AABBCCDD-EEFF0011-22334455-66778899";
        var bcryptHash = BCrypt.Net.BCrypt.HashPassword(plain, workFactor: 10);

        await limiter.EnterAsync("hold");
        try
        {
            var overload = await Assert.ThrowsAsync<Core.Exceptions.PasswordVerifyOverloadedException>(
                () => hasher.VerifyAsync(plain, bcryptHash));
            Assert.NotNull(overload);
        }
        finally
        {
            limiter.Exit("hold", 0);
        }

        Assert.True(await hasher.VerifyAsync(plain, bcryptHash));
    }

    [SkippableFact]
    public async Task RecoveryCode_ConsumeOnce_RejectsReuse()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var password = "Passw0rd!";
        var user = await SeedUserAsync(db, $"rc-{suffix}", $"rc-{suffix}@ex.com", password);

        var hasher = AuthTestFactories.CreatePasswordHasher();
        var mfa = new MfaService(
            db, hasher, CreateRecoveryHasher(), CreateMfaProtector(),
            new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance),
            redis.Cache, NullLogger<MfaService>.Instance);
        var (sharedKey, _, codes) = await mfa.BeginSetupAsync(user.Id, password);
        Assert.Equal(8, codes.Length);
        var totp = new Totp(Base32Encoding.ToBytes(sharedKey)).ComputeTotp();
        Assert.True((await mfa.ConfirmSetupAsync(user.Id, totp)).Succeeded);

        Assert.True(await mfa.TryConsumeRecoveryCodeAsync(user.Id, codes[0]));
        Assert.False(await mfa.TryConsumeRecoveryCodeAsync(user.Id, codes[0]));

        var stored = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.Contains("v1:", stored.RecoveryCodesHashJson, StringComparison.Ordinal);
        Assert.DoesNotContain("$2", stored.RecoveryCodesHashJson); // 非BCrypt
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
        var security = new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance);
        return new AuthService(
            db,
            AuthTestFactories.CreatePasswordHasher(),
            tokens,
            tokens,
            new FixedDeviceInfo("mfa-device"),
            new EmailVerificationService(new NoopEmail(), redis.Cache, redis.Cache),
            new TsidGeneratorService(),
            security,
            new MfaService(db, AuthTestFactories.CreatePasswordHasher(), CreateRecoveryHasher(), CreateMfaProtector(), security, redis.Cache, NullLogger<MfaService>.Instance),
            redis.Cache,
            redis.Cache,
            new NoopNotify(),
            CreateTrusted(db),
            NoopLoginRiskAnalyzer.Instance,
            Options.Create(new RealtimeGatewayOptions { Host = "127.0.0.1", Port = 8888, Name = "test" }),
            NullLogger<AuthService>.Instance);
    }

    private (UserAccountService Account, IEmailVerificationService Email, TokenService Sessions)
        CreateAccount(UserDbContext db)
    {
        var hasher = AuthTestFactories.CreatePasswordHasher();
        var repo = new UserRepository(db, new TsidGeneratorService());
        var email = new EmailVerificationService(new NoopEmail(), redis.Cache, redis.Cache);
        var tokens = CreateTokenService("mfa-device");
        var security = new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance);
        var account = new UserAccountService(
            repo, hasher, email, tokens, new FixedDeviceInfo("mfa-device"),
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
            CreateTrusted(db),
            Options.Create(new ProfileOptions()),
            NullLogger<UserAccountService>.Instance);
        return (account, email, tokens);
    }

    private TrustedDeviceService CreateTrusted(UserDbContext db)
    {
        var security = new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance);
        var hasher = AuthTestFactories.CreatePasswordHasher();
        var mfa = new MfaService(db, hasher, CreateRecoveryHasher(), CreateMfaProtector(), security, redis.Cache, NullLogger<MfaService>.Instance);
        return new TrustedDeviceService(
            db,
            security,
            hasher,
            mfa,
            redis.Cache,
            redis.Cache,
            new FixedDeviceInfo(AuthTestFactories.StableDeviceId("mfa-tests")),
            new Microsoft.AspNetCore.Http.HttpContextAccessor(),
            Options.Create(new TrustedDeviceOptions()),
            NullLogger<TrustedDeviceService>.Instance);
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
            redis.Cache,
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

    private static IRecoveryCodeHasher CreateRecoveryHasher()
        => new HmacRecoveryCodeHasher(
            Options.Create(new SecurityOptions { SecretEncryptionKey = "test-mfa-encryption-key", KeyVersion = 1 }),
            Options.Create(new JwtSettings { Secret = "test-mfa-jwt-secret-please-change" }),
            new TestHostEnvironment(),
            AuthTestFactories.CreateCpuLimiter(),
            NullLogger<HmacRecoveryCodeHasher>.Instance);

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
        var hasher = AuthTestFactories.CreatePasswordHasher();
        var user = new ApplicationUser
        {
            Id = new TsidGeneratorService().GenerateTsid(),
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            PasswordHash = await hasher.HashPasswordAsync(password),
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
            CancellationToken cancellationToken = default, string? idempotencyKey = null)
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
