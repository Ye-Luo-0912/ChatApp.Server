using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models;
using Core.Models.Email;
using Core.Models.Identity;
using Core.Services;
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
public sealed class AccountSecurityTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [SkippableFact]
    public async Task EmailChange_RequestConfirmCancel_AndConcurrency()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (account, emailService, sessions) = CreateAccountStack(db);

        var user = await SeedUserAsync(db, $"chg-{suffix}", $"old-{suffix}@example.com", "Password1!");
        var other = await SeedUserAsync(db, $"oth-{suffix}", $"taken-{suffix}@example.com", "Password1!");

        // 占用冲突
        var conflict = await account.RequestEmailChangeAsync(user.Id, other.Email!);
        Assert.False(conflict!.Succeeded);

        var request = await account.RequestEmailChangeAsync(user.Id, $"new-{suffix}@example.com");
        Assert.True(request!.Succeeded);

        var pending = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Equal($"new-{suffix}@example.com", pending.PendingEmail);
        Assert.Equal($"old-{suffix}@example.com", pending.Email);

        var cancel = await account.CancelEmailChangeAsync(user.Id);
        Assert.True(cancel!.Succeeded);
        pending = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Null(pending.PendingEmail);

        Assert.True((await account.RequestEmailChangeAsync(user.Id, $"final-{suffix}@example.com"))!.Succeeded);
        var code = await redis.Cache.StringGetAsync($"EmailCode:{EmailCodePurpose.ChangeEmail}:final-{suffix}@example.com");
        Assert.False(string.IsNullOrEmpty(code));

        var tokenService = CreateTokenService("device-a");
        await tokenService.IssueLoginTokensAsync(user, ["User"]);
        var otherDevice = CreateTokenService("device-b");
        await otherDevice.IssueLoginTokensAsync(user, ["User"]);

        var confirm = await account.ConfirmEmailChangeAsync(user.Id, code!);
        Assert.True(confirm!.Succeeded);

        var updated = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Equal($"final-{suffix}@example.com", updated.Email);
        Assert.True(updated.EmailConfirmed);
        Assert.Null(updated.PendingEmail);

        var remaining = await sessions.ListSessionsAsync(user.Id.ToString());
        Assert.True(remaining.Count <= 1);
    }

    [SkippableFact]
    public async Task ResetPassword_CodeIsOneShot_AndRevokesSessions()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var emailVerification = new EmailVerificationService(new RecordingEmailSender(), redis.Cache);
        var auth = CreateAuthService(db, emailVerification);
        var user = await SeedUserAsync(db, $"reset-{suffix}", $"reset-{suffix}@example.com", "OldPass1!");

        var tokenService = CreateTokenService("reset-device");
        await tokenService.IssueLoginTokensAsync(user, ["User"]);

        Assert.True((await emailVerification.SendEmailCodeAsync($"reset-{suffix}@example.com", EmailCodePurpose.ResetPassword, default)).IsSuccess);
        var code = await redis.Cache.StringGetAsync($"EmailCode:{EmailCodePurpose.ResetPassword}:reset-{suffix}@example.com");
        Assert.False(string.IsNullOrEmpty(code));

        var first = await auth.ResetPasswordAsync($"reset-{suffix}@example.com", code!, "NewPass1!");
        Assert.True(first.Succeeded);

        var second = await auth.ResetPasswordAsync($"reset-{suffix}@example.com", code!, "Another1!");
        Assert.False(second.Succeeded);

        var sessions = await tokenService.ListSessionsAsync(user.Id.ToString());
        Assert.Empty(sessions);
    }

    [SkippableFact]
    public async Task ChangePassword_RevokesOtherDevices()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (account, _, _) = CreateAccountStack(db, currentDeviceId: "keep-device");
        var user = await SeedUserAsync(db, $"pwd-{suffix}", $"pwd-{suffix}@example.com", "OldPass1!");

        await CreateTokenService("keep-device").IssueLoginTokensAsync(user, ["User"]);
        await CreateTokenService("kick-device").IssueLoginTokensAsync(user, ["User"]);

        var result = await account.ChangePasswordAsync(user.Id, "OldPass1!", "NewPass1!");
        Assert.True(result!.Succeeded);

        var sessions = await CreateTokenService("keep-device").ListSessionsAsync(user.Id.ToString());
        Assert.Single(sessions);
        Assert.Equal("keep-device", sessions[0].DeviceId);
    }

    [SkippableFact]
    public async Task Sessions_ListRevokeAndForceLogout()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (account, _, _) = CreateAccountStack(db, currentDeviceId: "d1");
        var user = await SeedUserAsync(db, $"sess-{suffix}", $"sess-{suffix}@example.com", "Pass123!");

        await CreateTokenService("d1").IssueLoginTokensAsync(user, ["User"]);
        await CreateTokenService("d2").IssueLoginTokensAsync(user, ["User"]);

        var listed = await account.ListSessionsAsync(user.Id, "d1");
        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, s => s.IsCurrent);

        await account.RevokeSessionAsync(user.Id, "d2");
        listed = await account.ListSessionsAsync(user.Id, "d1");
        Assert.Single(listed);

        var revoked = await account.ForceLogoutAsync(user.Id, "test", null);
        Assert.True(revoked >= 1);
        Assert.Empty(await account.ListSessionsAsync(user.Id, "d1"));
    }

    [SkippableFact]
    public async Task Disable_LocksAndRevokesAllSessions()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (account, _, _) = CreateAccountStack(db);
        var user = await SeedUserAsync(db, $"dis-{suffix}", $"dis-{suffix}@example.com", "Pass123!");
        await CreateTokenService("dx").IssueLoginTokensAsync(user, ["User"]);

        Assert.True((await account.DisableAsync(user.Id, "test", null))!.Succeeded);
        var locked = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.True(locked.LockoutEnd > DateTimeOffset.UtcNow);
        Assert.Empty(await CreateTokenService("dx").ListSessionsAsync(user.Id.ToString()));
    }

    [SkippableFact]
    public async Task OperationCanceled_IsNotWrapped()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var (account, _, _) = CreateAccountStack(db);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => account.GetByIdAsync(1, cts.Token));
    }

    private (UserAccountService Account, IEmailVerificationService Email, ISessionStore Sessions)
        CreateAccountStack(UserDbContext db, string currentDeviceId = "device-a")
    {
        var hasher = new BcryptPasswordHasher();
        var repo = new UserRepository(db, new TsidGeneratorService());
        var email = new EmailVerificationService(new RecordingEmailSender(), redis.Cache);
        var tokens = CreateTokenService(currentDeviceId);
        var account = new UserAccountService(
            repo, hasher, email, tokens, new FixedDeviceInfo(currentDeviceId),
            new LocalAvatarStorage(
                Options.Create(new AvatarStorageOptions()),
                redis.Cache,
                new AvatarReencodeQueue(
                    Options.Create(new AvatarStorageOptions()),
                    new AvatarReencodeMetrics()),
                NullLogger<LocalAvatarStorage>.Instance),
            new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance),
            new NoopSecurityNotificationService(),
            Options.Create(new ProfileOptions()),
            NullLogger<UserAccountService>.Instance);
        return (account, email, tokens);
    }

    private AuthService CreateAuthService(UserDbContext db, IEmailVerificationService email)
    {
        var tokens = CreateTokenService("auth-device");
        var mfa = new MfaService(
            db,
            new BcryptPasswordHasher(),
            new AesGcmMfaSecretProtector(
                Options.Create(new SecurityOptions { SecretEncryptionKey = "test-mfa-encryption-key", KeyVersion = 1 }),
                Options.Create(new JwtSettings { Secret = "test-mfa-jwt-secret-please-change" }),
                new TestHostEnvironment(),
                NullLogger<AesGcmMfaSecretProtector>.Instance),
            new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance),
            NullLogger<MfaService>.Instance);
        return new AuthService(
            db,
            new BcryptPasswordHasher(),
            tokens,
            tokens,
            new FixedDeviceInfo("auth-device"),
            email,
            new TsidGeneratorService(),
            new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance),
            mfa,
            redis.Cache,
            new NoopSecurityNotificationService(),
            Options.Create(new RealtimeGatewayOptions { Host = "127.0.0.1", Port = 8888, Name = "test" }),
            NullLogger<AuthService>.Instance);
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
        });

        return new TokenService(
            redis.Cache,
            new FixedDeviceInfo(deviceId),
            jwt,
            NullLogger<TokenService>.Instance);
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

    private sealed class RecordingEmailSender : IEmailSender
    {
        public Task<EmailResult> SendEmailAsync(
            string to, string subject, string body, bool isHtml = true, CancellationToken cancellation = default)
            => Task.FromResult(new EmailResult { IsSuccess = true });

        public Task<EmailResult> EnqueueEmailAsync(
            string to, string subject, string body, bool isHtml = true,
            string? emailType = null, string? idempotencyKey = null, CancellationToken cancellation = default)
            => SendEmailAsync(to, subject, body, isHtml, cancellation);

        public Task<EmailResult> SendVerificationEmailAsync(
            string to, string username, string verificationToken, CancellationToken cancellation)
            => SendEmailAsync(to, "v", verificationToken, true, cancellation);
    }

    private sealed class NoopSecurityNotificationService : ISecurityNotificationService
    {
        public void StageNotify(long userId, string type, string title, string body, bool preferEmail) { }

        public Task NotifyAsync(
            long userId, string type, string title, string body, bool preferEmail,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
