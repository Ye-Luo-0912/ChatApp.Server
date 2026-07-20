using System.Security.Cryptography;
using System.Text.Json;
using Core.Interfaces;
using Core.Models.Auth;
using Core.Models.Identity;
using Core.Models.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OtpNet;

namespace Infrastructure.Services.Auth;

public sealed class MfaService(
    UserDbContext db,
    IPasswordHasher passwordHasher,
    IMfaSecretProtector secretProtector,
    ISecurityEventStore securityEventStore,
    ILogger<MfaService> logger) : IMfaService
{
    private const string Issuer = "ChatApp";
    private const int MaxRecoveryConsumeAttempts = 3;

    public async Task<(string SharedKey, string OtpAuthUri, string[] RecoveryCodes)> BeginSetupAsync(
        long userId, string password, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
                   ?? throw new InvalidOperationException("用户不存在");

        if (string.IsNullOrWhiteSpace(password)
            || string.IsNullOrWhiteSpace(user.PasswordHash)
            || !passwordHasher.VerifyPassword(password, user.PasswordHash))
            throw new UnauthorizedAccessException("密码验证失败");

        var key = KeyGeneration.GenerateRandomKey(20);
        var base32 = Base32Encoding.ToString(key);
        var codes = Enumerable.Range(0, 8).Select(_ => GenerateRecoveryCode()).ToArray();

        // 仅写入待确认字段，保留已启用的旧 MFA
        user.PendingTotpSecret = secretProtector.Protect(base32);
        user.PendingRecoveryCodesHashJson =
            JsonSerializer.Serialize(codes.Select(passwordHasher.HashPassword).ToArray());
        await db.SaveChangesAsync(cancellationToken);

        var account = user.Email ?? user.UserName ?? userId.ToString();
        var uri = new OtpUri(OtpType.Totp, base32, account, Issuer).ToString();

        logger.LogInformation("用户 {UserId} 开始 MFA 设置（待确认）", userId);
        return (base32, uri, codes);
    }

    public async Task<AuthOperationResult> ConfirmSetupAsync(
        long userId, string code, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null) return AuthOperationResult.Fail("NotFound", "用户不存在");
        if (string.IsNullOrWhiteSpace(user.PendingTotpSecret))
            return AuthOperationResult.Fail("NoSetup", "请先开始 MFA 设置");

        string pendingPlain;
        try
        {
            pendingPlain = secretProtector.Unprotect(user.PendingTotpSecret);
        }
        catch
        {
            return AuthOperationResult.Fail("CorruptSecret", "待确认密钥无效，请重新开始设置");
        }

        if (!VerifyTotpPlain(pendingPlain, code))
            return AuthOperationResult.Fail("InvalidCode", "验证码无效");

        user.TotpSecret = user.PendingTotpSecret;
        user.RecoveryCodesHashJson = user.PendingRecoveryCodesHashJson;
        user.PendingTotpSecret = null;
        user.PendingRecoveryCodesHashJson = null;
        user.TwoFactorEnabled = true;
        await db.SaveChangesAsync(cancellationToken);
        await securityEventStore.RecordAsync(
            userId, SecurityEventType.MfaEnabled, detail: "confirm-setup", cancellationToken: cancellationToken);
        return AuthOperationResult.Success();
    }

    public async Task<AuthOperationResult> DisableAsync(
        long userId, string password, string codeOrRecovery, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null) return AuthOperationResult.Fail("NotFound", "用户不存在");
        if (!user.TwoFactorEnabled || string.IsNullOrWhiteSpace(user.TotpSecret))
            return AuthOperationResult.Fail("NotEnabled", "未启用 MFA");

        if (string.IsNullOrWhiteSpace(password)
            || string.IsNullOrWhiteSpace(user.PasswordHash)
            || !passwordHasher.VerifyPassword(password, user.PasswordHash))
            return AuthOperationResult.Fail("InvalidPassword", "密码验证失败");

        var ok = VerifyTotpForUser(user, codeOrRecovery)
                 || await TryConsumeRecoveryCodeAsync(userId, codeOrRecovery, cancellationToken);
        if (!ok)
            return AuthOperationResult.Fail("InvalidCode", "验证码或恢复码无效");

        user.TwoFactorEnabled = false;
        user.TotpSecret = null;
        user.RecoveryCodesHashJson = null;
        user.PendingTotpSecret = null;
        user.PendingRecoveryCodesHashJson = null;
        await db.SaveChangesAsync(cancellationToken);
        await securityEventStore.RecordAsync(
            userId, SecurityEventType.MfaDisabled, detail: "user-disable", cancellationToken: cancellationToken);
        return AuthOperationResult.Success();
    }

    public bool VerifyTotp(string sharedKey, string code) => VerifyTotpPlain(sharedKey, code);

    public bool VerifyTotpForUser(ApplicationUser user, string code)
    {
        if (user is null || string.IsNullOrWhiteSpace(user.TotpSecret))
            return false;
        try
        {
            var plain = secretProtector.Unprotect(user.TotpSecret);
            return VerifyTotpPlain(plain, code);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TryConsumeRecoveryCodeAsync(
        long userId, string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            for (var attempt = 0; attempt < MaxRecoveryConsumeAttempts; attempt++)
            {
                await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
                var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
                if (user is null || string.IsNullOrWhiteSpace(user.RecoveryCodesHashJson))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return false;
                }

                var oldJson = user.RecoveryCodesHashJson;
                var hashes = JsonSerializer.Deserialize<string[]>(oldJson) ?? [];
                var matchIndex = -1;
                for (var i = 0; i < hashes.Length; i++)
                {
                    if (passwordHasher.VerifyPassword(code.Trim(), hashes[i]))
                    {
                        matchIndex = i;
                        break;
                    }
                }

                if (matchIndex < 0)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return false;
                }

                var remaining = hashes.Where((_, i) => i != matchIndex).ToArray();
                var newJson = JsonSerializer.Serialize(remaining);

                // 乐观并发：仅当哈希列表未被并发修改时提交
                var updated = await db.Users
                    .Where(u => u.Id == userId && u.RecoveryCodesHashJson == oldJson)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(u => u.RecoveryCodesHashJson, newJson),
                        cancellationToken);

                if (updated == 1)
                {
                    await tx.CommitAsync(cancellationToken);
                    return true;
                }

                await tx.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }

            return false;
        });
    }

    public async Task<(AuthOperationResult Result, string[]? Codes)> RegenerateRecoveryCodesAsync(
        long userId, string password, string codeOrRecovery, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return (AuthOperationResult.Fail("NotFound", "用户不存在"), null);
        if (!user.TwoFactorEnabled || string.IsNullOrWhiteSpace(user.TotpSecret))
            return (AuthOperationResult.Fail("NotEnabled", "未启用 MFA"), null);

        if (string.IsNullOrWhiteSpace(password)
            || string.IsNullOrWhiteSpace(user.PasswordHash)
            || !passwordHasher.VerifyPassword(password, user.PasswordHash))
            return (AuthOperationResult.Fail("InvalidPassword", "密码验证失败"), null);

        var ok = VerifyTotpForUser(user, codeOrRecovery)
                 || await TryConsumeRecoveryCodeAsync(userId, codeOrRecovery, cancellationToken);
        if (!ok)
            return (AuthOperationResult.Fail("InvalidCode", "验证码或恢复码无效"), null);

        // 消费恢复码后需重新加载用户
        user = await db.Users.FirstAsync(u => u.Id == userId, cancellationToken);
        var codes = Enumerable.Range(0, 8).Select(_ => GenerateRecoveryCode()).ToArray();
        user.RecoveryCodesHashJson =
            JsonSerializer.Serialize(codes.Select(passwordHasher.HashPassword).ToArray());
        await db.SaveChangesAsync(cancellationToken);
        await securityEventStore.RecordAsync(
            userId, SecurityEventType.MfaRecoveryCodesRegenerated,
            detail: "regenerate", cancellationToken: cancellationToken);
        return (AuthOperationResult.Success(), codes);
    }

    private static bool VerifyTotpPlain(string sharedKey, string code)
    {
        if (string.IsNullOrWhiteSpace(sharedKey) || string.IsNullOrWhiteSpace(code))
            return false;
        try
        {
            var totp = new Totp(Base32Encoding.ToBytes(sharedKey));
            return totp.VerifyTotp(code.Trim(), out _, new VerificationWindow(1, 1));
        }
        catch
        {
            return false;
        }
    }

    private static string GenerateRecoveryCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(5);
        return Convert.ToHexString(bytes);
    }
}
