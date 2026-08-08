using System.Security.Cryptography;
using System.Text.Json;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Models;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public sealed class PhoneVerificationService(
    IPhoneVerificationSender sender,
    IOneTimeStateStore oneTimeState,
    ICacheValueStore cache,
    IAtomicCacheStore atomicCache,
    IOptions<PhoneVerificationOptions> options) : IPhoneVerificationService
{
    private const string Purpose = "ChangePhone";
    private const int CodeDigitsMin = 100000;
    private const int CodeDigitsMax = 1000000;

    public async Task<(bool Succeeded, string? Error)> SendCodeAsync(
        string e164PhoneNumber,
        CancellationToken cancellationToken = default)
    {
        var normalized = PhoneNumberNormalizer.TryNormalizeE164(e164PhoneNumber);
        if (normalized is null)
            return (false, "手机号必须是 E.164 格式，例如 +8613800138000");

        var opts = options.Value;
        var lifetime = TimeSpan.FromMinutes(Math.Clamp(opts.CodeLifetimeMinutes, 1, 15));
        var cooldown = TimeSpan.FromSeconds(Math.Clamp(opts.ResendCooldownSeconds, 10, 300));
        var key = GetCodeKey(normalized);
        var cooldownKey = GetCooldownKey(normalized);
        if (!await atomicCache.StringSetIfNotExistsAsync(
                cooldownKey, "1", cooldown, cancellationToken).ConfigureAwait(false))
            return (false, "操作太频繁，请稍后再试");

        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
        var code = await oneTimeState.PeekAsync(key, cancellationToken).ConfigureAwait(false);
        code = Unquote(code);
        var created = false;
        if (string.IsNullOrWhiteSpace(code))
        {
            code = RandomNumberGenerator.GetInt32(CodeDigitsMin, CodeDigitsMax).ToString();
            await oneTimeState.IssueAsync(key, code, expiresAt, cancellationToken).ConfigureAwait(false);
            await cache.StringSetAsync(
                    GetExpiryKey(normalized),
                    expiresAt.ToUnixTimeMilliseconds().ToString(),
                    lifetime,
                    cancellationToken)
                .ConfigureAwait(false);
            created = true;
        }

        if (await sender.SendAsync(normalized, code, cancellationToken).ConfigureAwait(false))
            return (true, null);

        await cache.RemoveAsync(cooldownKey, CancellationToken.None).ConfigureAwait(false);
        if (created)
        {
            await oneTimeState.TryConsumeIfEqualAsync(key, code, CancellationToken.None)
                .ConfigureAwait(false);
        }

        return (false, "短信发送失败，请稍后重试");
    }

    public async Task<(bool Succeeded, PhoneVerificationClaim? Claim, string? Error)> ClaimCodeAsync(
        string e164PhoneNumber,
        string code,
        CancellationToken cancellationToken = default)
    {
        var normalized = PhoneNumberNormalizer.TryNormalizeE164(e164PhoneNumber);
        if (normalized is null || string.IsNullOrWhiteSpace(code))
            return (false, null, "手机号或验证码无效");

        var key = GetCodeKey(normalized);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(
            Math.Clamp(options.Value.CodeLifetimeMinutes, 1, 15));
        var expiryRaw = await cache.StringGetAsync(
                GetExpiryKey(normalized), cancellationToken)
            .ConfigureAwait(false);
        if (long.TryParse(expiryRaw, out var expiryMs))
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiryMs);
        if (expiresAt <= DateTimeOffset.UtcNow)
            return (false, null, "验证码已过期");
        var claimKey = $"PhoneCodeClaim:{normalized}";
        var claimed = await oneTimeState.TryClaimAsync<string>(
                key, claimKey, expiresAt, cancellationToken)
            .ConfigureAwait(false);
        if (claimed is null || !string.Equals(claimed.Payload, code.Trim(), StringComparison.Ordinal))
        {
            if (claimed is not null)
                await oneTimeState.RestoreClaimAsync(key, claimKey, expiresAt, CancellationToken.None)
                    .ConfigureAwait(false);
            return (false, null, "验证码无效或已过期");
        }

        return (true, new PhoneVerificationClaim(normalized, claimed.Payload, expiresAt), null);
    }

    public async Task CompleteCodeAsync(
        PhoneVerificationClaim claim,
        CancellationToken cancellationToken = default)
    {
        await oneTimeState.CompleteClaimAsync(
                GetClaimKey(claim.PhoneNumber), cancellationToken)
            .ConfigureAwait(false);
        await cache.RemoveAsync(GetExpiryKey(claim.PhoneNumber), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RestoreCodeAsync(
        PhoneVerificationClaim claim,
        CancellationToken cancellationToken = default)
    {
        var remaining = claim.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return;
        await oneTimeState.RestoreClaimAsync(
                GetCodeKey(claim.PhoneNumber),
                GetClaimKey(claim.PhoneNumber),
                claim.ExpiresAt,
                cancellationToken)
            .ConfigureAwait(false);
        await cache.StringSetAsync(
                GetExpiryKey(claim.PhoneNumber),
                claim.ExpiresAt.ToUnixTimeMilliseconds().ToString(),
                remaining,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string GetCodeKey(string phone) => $"PhoneCode:{Purpose}:{phone}";
    private static string GetCooldownKey(string phone) => $"PhoneCodeCooldown:{Purpose}:{phone}";
    private static string GetClaimKey(string phone) => $"PhoneCodeClaim:{phone}";
    private static string GetExpiryKey(string phone) => $"PhoneCodeExpiry:{Purpose}:{phone}";

    private static string? Unquote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(value); }
            catch (JsonException) { }
        }
        return value;
    }
}
