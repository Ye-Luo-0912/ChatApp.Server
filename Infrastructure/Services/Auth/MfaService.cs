using System.Text.Json;
using Core.Caching;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Interfaces.Cache;
using Core.Models.Auth;
using Core.Models.Identity;
using Core.Models.Security;
using Core.Models.Token;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OtpNet;

namespace Infrastructure.Services.Auth;

public sealed class MfaService(
    UserDbContext db,
    IPasswordHasher passwordHasher,
    IRecoveryCodeHasher recoveryCodeHasher,
    IMfaSecretProtector secretProtector,
    ISecurityEventStore securityEventStore,
    IAtomicCacheStore cache,
    ILogger<MfaService> logger,
    ISecurityVersionAdvancer? securityVersions = null,
    IAuthSnapshotStore? authSnapshots = null,
    ISecurityMutationCoordinator? securityMutations = null) : IMfaService
{
    private const string Issuer = "ChatApp";
    private readonly ISecurityMutationCoordinator _securityMutationCoordinator =
        securityMutations ?? new SecurityMutationCoordinator(
            db,
            securityVersions ?? new SecurityVersionAdvancer(db),
            NullLogger<SecurityMutationCoordinator>.Instance);
    private const int RecoveryCodeCount = 8;
    private static readonly TimeSpan TotpUsedTtl = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan RecoveryClaimTtl = TimeSpan.FromMinutes(5);

    public async Task<(string SharedKey, string OtpAuthUri, string[] RecoveryCodes)> BeginSetupAsync(
        long userId, string password, CancellationToken cancellationToken = default)
    {
        _ = securityEventStore; // retained for compatibility with direct test construction
        if (!await IsAuthoritativelyAllowedAsync(userId, cancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("账号当前不可执行 MFA 操作");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
                   ?? throw new InvalidOperationException("用户不存在");

        if (string.IsNullOrWhiteSpace(password)
            || string.IsNullOrWhiteSpace(user.PasswordHash)
            || !await passwordHasher.VerifyPasswordAsync(password, user.PasswordHash, cancellationToken))
            throw new UnauthorizedAccessException("密码验证失败");

        var key = KeyGeneration.GenerateRandomKey(20);
        var base32 = Base32Encoding.ToString(key);
        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => recoveryCodeHasher.GeneratePlainCode()).ToArray();

        // 仅写入待确认字段，保留已启用的旧 MFA
        user.PendingTotpSecret = secretProtector.Protect(base32);
        user.PendingRecoveryCodesHashJson =
            JsonSerializer.Serialize(codes.Select(recoveryCodeHasher.Hash).ToArray());
        await db.SaveChangesAsync(cancellationToken);

        var account = user.Email ?? user.UserName ?? userId.ToString();
        var uri = new OtpUri(OtpType.Totp, base32, account, Issuer).ToString();

        logger.LogInformation("用户 {UserId} 开始 MFA 设置（待确认）", userId);
        return (base32, uri, codes);
    }

    public async Task<AuthOperationResult> ConfirmSetupAsync(
        long userId, string code, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthoritativelyAllowedAsync(userId, cancellationToken).ConfigureAwait(false))
            return AuthOperationResult.Fail("AccountUnavailable", "账号当前不可执行 MFA 操作");

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

        if (!TryVerifyTotpPlain(pendingPlain, code, out _))
            return AuthOperationResult.Fail("InvalidCode", "验证码无效");

        // 设置确认不占用登录防重放时间步：PendingTotpSecret 清除即一次性。
        user.TotpSecret = user.PendingTotpSecret;
        user.RecoveryCodesHashJson = user.PendingRecoveryCodesHashJson;
        user.PendingTotpSecret = null;
        user.PendingRecoveryCodesHashJson = null;
        user.TwoFactorEnabled = true;
        if (!await SaveAndAdvanceSecurityVersionAsync(
                    userId, SecurityEventType.MfaEnabled, "confirm-setup", cancellationToken)
                .ConfigureAwait(false))
            return AuthOperationResult.Fail("UpdateFailed", "MFA 状态保存失败");
        return AuthOperationResult.Success();
    }

    public async Task<AuthOperationResult> DisableAsync(
        long userId, string password, string codeOrRecovery, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthoritativelyAllowedAsync(userId, cancellationToken).ConfigureAwait(false))
            return AuthOperationResult.Fail("AccountUnavailable", "账号当前不可执行 MFA 操作");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null) return AuthOperationResult.Fail("NotFound", "用户不存在");
        if (!user.TwoFactorEnabled || string.IsNullOrWhiteSpace(user.TotpSecret))
            return AuthOperationResult.Fail("NotEnabled", "未启用 MFA");

        if (string.IsNullOrWhiteSpace(password)
            || string.IsNullOrWhiteSpace(user.PasswordHash)
            || !await passwordHasher.VerifyPasswordAsync(password, user.PasswordHash, cancellationToken))
            return AuthOperationResult.Fail("InvalidPassword", "密码验证失败");

        var totpClaim = await TryClaimTotpForUserAsync(user, codeOrRecovery, cancellationToken)
            .ConfigureAwait(false);
        if (totpClaim is not null)
        {
            try
            {
                user.TwoFactorEnabled = false;
                user.TotpSecret = null;
                user.RecoveryCodesHashJson = null;
                user.PendingTotpSecret = null;
                user.PendingRecoveryCodesHashJson = null;
                if (!await SaveAndAdvanceSecurityVersionAsync(
                            userId, SecurityEventType.MfaDisabled, "user-disable", cancellationToken)
                        .ConfigureAwait(false))
                {
                    await RestoreTotpClaimAsync(totpClaim, CancellationToken.None)
                        .ConfigureAwait(false);
                    return AuthOperationResult.Fail("UpdateFailed", "MFA 状态保存失败");
                }

                return AuthOperationResult.Success();
            }
            catch
            {
                await RestoreTotpClaimAsync(totpClaim, CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
        }

        // Recovery-code consumption and disabling MFA share one transaction.
        // A failure after verification therefore cannot burn the one-time code.
        return await DisableWithRecoveryCodeAsync(userId, codeOrRecovery, cancellationToken)
            .ConfigureAwait(false);
    }

    public bool VerifyTotp(string sharedKey, string code) =>
        TryVerifyTotpPlain(sharedKey, code, out _);

    public bool VerifyTotpForUser(ApplicationUser user, string code)
    {
        // 无防重放的同步校验仅用于只读探测；登录/敏感路径请用 TryVerifyAndConsumeTotpForUserAsync。
        if (user is null || string.IsNullOrWhiteSpace(user.TotpSecret))
            return false;
        try
        {
            var plain = secretProtector.Unprotect(user.TotpSecret);
            return TryVerifyTotpPlain(plain, code, out _);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TryVerifyAndConsumeTotpForUserAsync(
        ApplicationUser user, string code, CancellationToken cancellationToken = default)
    {
        // This compatibility API intentionally keeps the claim as the replay
        // marker. Callers that also mutate durable state should use the claim
        // API above so they can restore it on rollback.
        return await TryClaimTotpForUserAsync(user, code, cancellationToken)
            .ConfigureAwait(false) is not null;
    }

    public async Task<MfaVerificationClaim?> TryClaimTotpForUserAsync(
        ApplicationUser user, string code, CancellationToken cancellationToken = default)
    {
        if (user is null || string.IsNullOrWhiteSpace(user.TotpSecret))
            return null;
        try
        {
            var plain = secretProtector.Unprotect(user.TotpSecret);
            return await TryClaimTotpPlainAsync(user.Id, plain, code, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public Task RestoreTotpClaimAsync(
        MfaVerificationClaim claim, CancellationToken cancellationToken = default)
        => cache.TryStringCompareAndDeleteAsync(
            claim.Key, claim.Marker, cancellationToken);

    public async Task<MfaRecoveryCodeClaim?> TryClaimRecoveryCodeForUserAsync(
        long userId, string code, CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(code))
            return null;

        // A crashed request leaves a durable Claimed row. Reconcile only this
        // user's expired claims before inspecting the active code list; this
        // keeps recovery bounded and never extends the original claim TTL.
        await RestoreExpiredRecoveryCodeClaimsAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(RecoveryClaimTtl);
        var ownsTransaction = db.Database.IsRelational()
                              && db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                        $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {userId} FOR UPDATE""",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var user = await db.Users
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
                .ConfigureAwait(false);
            if (user is null || string.IsNullOrWhiteSpace(user.RecoveryCodesHashJson))
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            var originalJson = user.RecoveryCodesHashJson;
            var hashes = JsonSerializer.Deserialize<string[]>(originalJson) ?? [];
            var matchIndex = -1;
            for (var i = 0; i < hashes.Length; i++)
            {
                if (await recoveryCodeHasher.VerifyAsync(code, hashes[i], cancellationToken)
                        .ConfigureAwait(false))
                {
                    matchIndex = i;
                    break;
                }
            }

            if (matchIndex < 0)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            var remaining = hashes.Where((_, index) => index != matchIndex).ToArray();
            var remainingJson = JsonSerializer.Serialize(remaining);
            var claimToken = TokenBufferEncoding.CreateBase64Url(24);
            var entity = new MfaRecoveryCodeClaimEntity
            {
                UserId = userId,
                ClaimToken = claimToken,
                CodeDigest = hashes[matchIndex],
                OriginalCodesJson = originalJson,
                RemainingCodesJson = remainingJson,
                State = MfaRecoveryCodeClaimState.Claimed,
                ClaimedAt = now,
                ExpiresAt = expiresAt,
            };

            user.RecoveryCodesHashJson = remainingJson;
            db.MfaRecoveryCodeClaims.Add(entity);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new MfaRecoveryCodeClaim(
                entity.Id, userId, claimToken, expiresAt);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<long?> CompleteRecoveryCodeClaimAsync(
        MfaRecoveryCodeClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var ownsTransaction = db.Database.IsRelational()
                              && db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                        $"""SELECT 1 FROM "T_MfaRecoveryCodeClaim" WHERE "Id" = {claim.Id} FOR UPDATE""",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var entity = await db.MfaRecoveryCodeClaims
                .FirstOrDefaultAsync(
                    x => x.Id == claim.Id
                         && x.UserId == claim.UserId
                         && x.ClaimToken == claim.ClaimToken,
                    cancellationToken)
                .ConfigureAwait(false);
            if (entity is null || entity.State != MfaRecoveryCodeClaimState.Claimed)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            if (entity.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                var expiredUser = await db.Users
                    .FirstOrDefaultAsync(u => u.Id == claim.UserId, cancellationToken)
                    .ConfigureAwait(false);
                if (expiredUser is not null
                    && string.Equals(
                        expiredUser.RecoveryCodesHashJson,
                        entity.RemainingCodesJson,
                        StringComparison.Ordinal))
                {
                    expiredUser.RecoveryCodesHashJson = entity.OriginalCodesJson;
                }

                entity.State = MfaRecoveryCodeClaimState.Expired;
                entity.CompletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var mutation = await _securityMutationCoordinator.ExecuteAsync(
                    claim.UserId,
                    SecurityEventType.MfaRecoveryCodeUsed,
                    $"claim={claim.Id}",
                    _ =>
                    {
                        entity.State = MfaRecoveryCodeClaimState.Completed;
                        entity.CompletedAt = DateTimeOffset.UtcNow;
                        return Task.CompletedTask;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!mutation.Succeeded)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return mutation.SecurityVersion;
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> RestoreRecoveryCodeClaimAsync(
        MfaRecoveryCodeClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var ownsTransaction = db.Database.IsRelational()
                              && db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                        $"""SELECT 1 FROM "T_MfaRecoveryCodeClaim" WHERE "Id" = {claim.Id} FOR UPDATE""",
                        cancellationToken)
                    .ConfigureAwait(false);
                await db.Database.ExecuteSqlInterpolatedAsync(
                        $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {claim.UserId} FOR UPDATE""",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var entity = await db.MfaRecoveryCodeClaims
                .FirstOrDefaultAsync(
                    x => x.Id == claim.Id
                         && x.UserId == claim.UserId
                         && x.ClaimToken == claim.ClaimToken,
                    cancellationToken)
                .ConfigureAwait(false);
            if (entity is null
                || entity.State is not (MfaRecoveryCodeClaimState.Claimed
                    or MfaRecoveryCodeClaimState.Completed))
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            var user = await db.Users
                .FirstOrDefaultAsync(u => u.Id == claim.UserId, cancellationToken)
                .ConfigureAwait(false);
            if (user is not null
                && string.Equals(
                    user.RecoveryCodesHashJson,
                    entity.RemainingCodesJson,
                    StringComparison.Ordinal))
            {
                user.RecoveryCodesHashJson = entity.OriginalCodesJson;
            }

            entity.State = MfaRecoveryCodeClaimState.Restored;
            entity.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> TryConsumeRecoveryCodeAsync(
        long userId, string code, CancellationToken cancellationToken = default)
    {
        var claim = await TryClaimRecoveryCodeForUserAsync(userId, code, cancellationToken)
            .ConfigureAwait(false);
        if (claim is null)
            return false;

        try
        {
            if (await CompleteRecoveryCodeClaimAsync(claim, cancellationToken)
                    .ConfigureAwait(false) is { } )
                return true;

            await RestoreRecoveryCodeClaimAsync(claim, CancellationToken.None)
                .ConfigureAwait(false);
            return false;
        }
        catch
        {
            await RestoreRecoveryCodeClaimAsync(claim, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task<(AuthOperationResult Result, string[]? Codes)> RegenerateRecoveryCodesAsync(
        long userId, string password, string codeOrRecovery, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthoritativelyAllowedAsync(userId, cancellationToken).ConfigureAwait(false))
            return (AuthOperationResult.Fail("AccountUnavailable", "账号当前不可执行 MFA 操作"), null);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return (AuthOperationResult.Fail("NotFound", "用户不存在"), null);
        if (!user.TwoFactorEnabled || string.IsNullOrWhiteSpace(user.TotpSecret))
            return (AuthOperationResult.Fail("NotEnabled", "未启用 MFA"), null);

        if (string.IsNullOrWhiteSpace(password)
            || string.IsNullOrWhiteSpace(user.PasswordHash)
            || !await passwordHasher.VerifyPasswordAsync(password, user.PasswordHash, cancellationToken))
            return (AuthOperationResult.Fail("InvalidPassword", "密码验证失败"), null);

        var totpClaim = await TryClaimTotpForUserAsync(user, codeOrRecovery, cancellationToken)
            .ConfigureAwait(false);
        if (totpClaim is not null)
        {
            var codes = GenerateRecoveryCodes();
            try
            {
                user.RecoveryCodesHashJson =
                    JsonSerializer.Serialize(codes.Select(recoveryCodeHasher.Hash).ToArray());
                if (!await SaveAndAdvanceSecurityVersionAsync(
                            userId,
                            SecurityEventType.MfaRecoveryCodesRegenerated,
                            "regenerate",
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    await RestoreTotpClaimAsync(totpClaim, CancellationToken.None)
                        .ConfigureAwait(false);
                    return (AuthOperationResult.Fail("UpdateFailed", "恢复码保存失败"), null);
                }

                return (AuthOperationResult.Success(), codes);
            }
            catch
            {
                await RestoreTotpClaimAsync(totpClaim, CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
        }

        return await RegenerateRecoveryCodesWithCodeAsync(
                userId, codeOrRecovery, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AuthOperationResult> DisableWithRecoveryCodeAsync(
        long userId,
        string code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
            return AuthOperationResult.Fail("InvalidCode", "验证码或恢复码无效");

        db.ChangeTracker.Clear();
        var ownsTransaction = db.Database.IsRelational();
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {userId} FOR UPDATE""",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null || !user.TwoFactorEnabled
            || string.IsNullOrWhiteSpace(user.RecoveryCodesHashJson))
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return AuthOperationResult.Fail("InvalidCode", "验证码或恢复码无效");
        }

        var (matched, remaining) = await RemoveMatchingRecoveryCodeAsync(
                user.RecoveryCodesHashJson, code, cancellationToken)
            .ConfigureAwait(false);
        if (!matched)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return AuthOperationResult.Fail("InvalidCode", "验证码或恢复码无效");
        }

        user.TwoFactorEnabled = false;
        user.TotpSecret = null;
        user.RecoveryCodesHashJson = JsonSerializer.Serialize(remaining);
        user.PendingTotpSecret = null;
        user.PendingRecoveryCodesHashJson = null;
        var mutation = await _securityMutationCoordinator.ExecuteAsync(
                userId,
                SecurityEventType.MfaDisabled,
                "user-disable-recovery-code",
                static _ => Task.CompletedTask,
                cancellationToken)
            .ConfigureAwait(false);
        if (!mutation.Succeeded)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return AuthOperationResult.Fail("UpdateFailed", "MFA 状态保存失败");
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AuthOperationResult.Success();
    }

    private async Task<(AuthOperationResult Result, string[]? Codes)>
        RegenerateRecoveryCodesWithCodeAsync(
            long userId,
            string code,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
            return (AuthOperationResult.Fail("InvalidCode", "验证码或恢复码无效"), null);

        db.ChangeTracker.Clear();
        var ownsTransaction = db.Database.IsRelational();
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {userId} FOR UPDATE""",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null || !user.TwoFactorEnabled
            || string.IsNullOrWhiteSpace(user.RecoveryCodesHashJson))
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return (AuthOperationResult.Fail("InvalidCode", "验证码或恢复码无效"), null);
        }

        var (matched, _) = await RemoveMatchingRecoveryCodeAsync(
                user.RecoveryCodesHashJson, code, cancellationToken)
            .ConfigureAwait(false);
        if (!matched)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return (AuthOperationResult.Fail("InvalidCode", "验证码或恢复码无效"), null);
        }

        var codes = GenerateRecoveryCodes();
        user.RecoveryCodesHashJson =
            JsonSerializer.Serialize(codes.Select(recoveryCodeHasher.Hash).ToArray());
        var mutation = await _securityMutationCoordinator.ExecuteAsync(
                userId,
                SecurityEventType.MfaRecoveryCodesRegenerated,
                "regenerate-recovery-code",
                static _ => Task.CompletedTask,
                cancellationToken)
            .ConfigureAwait(false);
        if (!mutation.Succeeded)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return (AuthOperationResult.Fail("UpdateFailed", "恢复码保存失败"), null);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (AuthOperationResult.Success(), codes);
    }

    private async Task<(bool Matched, string[] Remaining)> RemoveMatchingRecoveryCodeAsync(
        string oldJson,
        string code,
        CancellationToken cancellationToken)
    {
        var hashes = JsonSerializer.Deserialize<string[]>(oldJson) ?? [];
        var matchIndex = -1;
        for (var i = 0; i < hashes.Length; i++)
        {
            if (await recoveryCodeHasher.VerifyAsync(code, hashes[i], cancellationToken)
                    .ConfigureAwait(false))
            {
                matchIndex = i;
                break;
            }
        }

        return matchIndex < 0
            ? (false, hashes)
            : (true, hashes.Where((_, index) => index != matchIndex).ToArray());
    }

    private async Task RestoreExpiredRecoveryCodeClaimsAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await db.MfaRecoveryCodeClaims
            .AsNoTracking()
            .Where(x => x.UserId == userId
                        && x.State == MfaRecoveryCodeClaimState.Claimed
                        && x.ExpiresAt <= now)
            .OrderBy(x => x.ExpiresAt)
            .Take(32)
            .Select(x => new MfaRecoveryCodeClaim(
                x.Id, x.UserId, x.ClaimToken, x.ExpiresAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var claim in expired)
        {
            try
            {
                await RestoreRecoveryCodeClaimAsync(claim, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "恢复过期 MFA 恢复码 Claim 失败 UserId={UserId} ClaimId={ClaimId}",
                    userId,
                    claim.Id);
            }
        }
    }

    private string[] GenerateRecoveryCodes()
        => Enumerable.Range(0, RecoveryCodeCount)
            .Select(_ => recoveryCodeHasher.GeneratePlainCode())
            .ToArray();

    private async Task<bool> SaveAndAdvanceSecurityVersionAsync(
        long userId,
        SecurityEventType eventType,
        string detail,
        CancellationToken cancellationToken)
        => (await _securityMutationCoordinator.ExecuteAsync(
                userId,
                eventType,
                detail,
                static _ => Task.CompletedTask,
                cancellationToken,
                options: new SecurityMutationOptions(RevokeTrustedDevices: true))
            .ConfigureAwait(false)).Succeeded;

    private async Task<bool> IsAuthoritativelyAllowedAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var snapshot = authSnapshots is not null
            ? await authSnapshots.GetAuthoritativeAsync(userId, cancellationToken)
                .ConfigureAwait(false)
            : await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new UserAuthSnapshot
                {
                    UserId = u.Id,
                    SecurityVersion = u.SecurityVersion,
                    AccountState = u.AccountState,
                    LockoutEnabled = u.LockoutEnabled,
                    LockoutEnd = u.LockoutEnd,
                    BanUntil = u.BanUntil,
                    DeletionScheduledAt = u.DeletionScheduledAt,
                })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        return snapshot?.IsAllowedAt(DateTimeOffset.UtcNow) == true;
    }

    private async Task<MfaVerificationClaim?> TryClaimTotpPlainAsync(
        long userId,
        string sharedKey,
        string code,
        CancellationToken cancellationToken)
    {
        if (!TryVerifyTotpPlain(sharedKey, code, out var timestep))
            return null;

        var key = $"{CacheConstants.TotpUsedPrefix}{userId}:{timestep}";
        var marker = TokenBufferEncoding.CreateBase64Url(16);
        var firstUse = await cache.StringSetIfNotExistsAsync(key, marker, TotpUsedTtl, cancellationToken)
            .ConfigureAwait(false);
        if (!firstUse)
        {
            logger.LogWarning("TOTP 时间步重放被拒绝 UserId={UserId} Timestep={Timestep}", userId, timestep);
            return null;
        }

        return new MfaVerificationClaim(key, marker, DateTimeOffset.UtcNow.Add(TotpUsedTtl));
    }

    private static bool TryVerifyTotpPlain(string sharedKey, string code, out long timestep)
    {
        timestep = 0;
        if (string.IsNullOrWhiteSpace(sharedKey) || string.IsNullOrWhiteSpace(code))
            return false;
        try
        {
            var totp = new Totp(Base32Encoding.ToBytes(sharedKey));
            return totp.VerifyTotp(code.Trim(), out timestep, new VerificationWindow(1, 1));
        }
        catch
        {
            return false;
        }
    }
}
