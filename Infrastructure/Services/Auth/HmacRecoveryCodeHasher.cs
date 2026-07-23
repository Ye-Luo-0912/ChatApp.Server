using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Interfaces;
using Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services.Auth;

/// <summary>MFA 恢复码：高熵随机明文 + 版本化 HMAC-SHA256 摘要（避免对恢复码做 BCrypt）。</summary>
public interface IRecoveryCodeHasher
{
    /// <summary>生成明文恢复码（仅展示一次）。</summary>
    string GeneratePlainCode();

    /// <summary>计算版本化 HMAC 摘要，格式 <c>v{version}:{base64url}</c>。</summary>
    string Hash(string plainCode);

    /// <summary>
    /// 恒定时间比对明文与已存摘要（支持当前/上一密钥版本；兼容旧版 BCrypt）。
    /// 遗留 BCrypt 走共享 Auth CPU 闸门；HMAC 路径不占闸门。
    /// </summary>
    Task<bool> VerifyAsync(string plainCode, string storedDigest, CancellationToken cancellationToken = default);

    /// <summary>是否为旧版 BCrypt 摘要（<c>$2a$</c>/<c>$2b$</c>/<c>$2y$</c>）。</summary>
    bool IsLegacyDigest(string storedDigest);

    /// <summary>JSON 数组中是否仍含旧版 BCrypt 摘要（用于提示客户端重新生成）。</summary>
    bool ContainsLegacyDigests(string? recoveryCodesHashJson);
}

public sealed class HmacRecoveryCodeHasher : IRecoveryCodeHasher
{
    private const int EntropyBytes = 16; // 128-bit
    private readonly Dictionary<int, byte[]> _keysByVersion;
    private readonly int _currentVersion;
    private readonly IAuthCpuLimiter _cpuLimiter;

    public HmacRecoveryCodeHasher(
        IOptions<SecurityOptions> security,
        IOptions<JwtSettings> jwt,
        IHostEnvironment env,
        IAuthCpuLimiter cpuLimiter,
        ILogger<HmacRecoveryCodeHasher> logger)
    {
        _cpuLimiter = cpuLimiter;
        var opts = security.Value;
        _currentVersion = opts.KeyVersion <= 0 ? 1 : opts.KeyVersion;
        _keysByVersion = new Dictionary<int, byte[]>();

        var primary = opts.SecretEncryptionKey;
        if (string.IsNullOrWhiteSpace(primary))
        {
            if (!env.IsDevelopment() && !env.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException(
                    "生产环境必须配置 Security:SecretEncryptionKey，禁止回退到 JwtSettings.Secret");
            }

            primary = string.IsNullOrWhiteSpace(jwt.Value.Secret)
                ? "dev-only-recovery-hmac-key-change-me"
                : jwt.Value.Secret;
            logger.LogWarning("Security:SecretEncryptionKey 未配置，恢复码 HMAC 已临时回退（仅 Development/Testing）");
        }

        _keysByVersion[_currentVersion] = Derive(primary);

        if (!string.IsNullOrWhiteSpace(opts.PreviousSecretEncryptionKey)
            && opts.PreviousKeyVersion is { } prevVer
            && prevVer > 0
            && prevVer != _currentVersion)
        {
            _keysByVersion[prevVer] = Derive(opts.PreviousSecretEncryptionKey);
        }
    }

    public string GeneratePlainCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(EntropyBytes);
        // 分组便于人工录入：XXXXXXXX-XXXXXXXX-XXXXXXXX-XXXXXXXX
        var hex = Convert.ToHexString(bytes);
        return $"{hex[..8]}-{hex[8..16]}-{hex[16..24]}-{hex[24..32]}";
    }

    public string Hash(string plainCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainCode);
        var digest = ComputeHmac(_keysByVersion[_currentVersion], Normalize(plainCode));
        return $"v{_currentVersion}:{Base64Url(digest)}";
    }

    public async Task<bool> VerifyAsync(
        string plainCode, string storedDigest, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(plainCode) || string.IsNullOrWhiteSpace(storedDigest))
            return false;

        // 旧版 BCrypt：兼容校验；与密码校验共用 CPU 闸门。
        if (IsLegacyDigest(storedDigest))
            return await VerifyBcryptCompatAsync(plainCode, storedDigest, cancellationToken).ConfigureAwait(false);

        if (!TrySplitVersioned(storedDigest, out var version, out var payload)
            || !_keysByVersion.TryGetValue(version, out var key))
            return false;

        byte[] expected;
        try
        {
            expected = FromBase64Url(payload);
        }
        catch
        {
            return false;
        }

        var actual = ComputeHmac(key, Normalize(plainCode));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public bool IsLegacyDigest(string storedDigest)
        => IsBcryptDigest(storedDigest);

    public bool ContainsLegacyDigests(string? recoveryCodesHashJson)
        => ContainsLegacyDigestsStatic(recoveryCodesHashJson);

    /// <summary>JSON 数组中是否仍含旧版 BCrypt 摘要。</summary>
    public static bool ContainsLegacyDigestsStatic(string? recoveryCodesHashJson)
    {
        if (string.IsNullOrWhiteSpace(recoveryCodesHashJson))
            return false;
        try
        {
            var hashes = JsonSerializer.Deserialize<string[]>(recoveryCodesHashJson);
            return hashes is { Length: > 0 } && hashes.Any(IsBcryptDigest);
        }
        catch
        {
            return recoveryCodesHashJson.Contains("$2a$", StringComparison.Ordinal)
                   || recoveryCodesHashJson.Contains("$2b$", StringComparison.Ordinal)
                   || recoveryCodesHashJson.Contains("$2y$", StringComparison.Ordinal);
        }
    }

    /// <summary>识别 bcrypt 摘要前缀。</summary>
    public static bool IsBcryptDigest(string digest)
        => digest.StartsWith("$2a$", StringComparison.Ordinal)
           || digest.StartsWith("$2b$", StringComparison.Ordinal)
           || digest.StartsWith("$2y$", StringComparison.Ordinal);

    /// <summary>
    /// 旧版可能以「带连字符原文」或「规范化大写」写入 BCrypt；展开候选后逐一校验。
    /// </summary>
    private async Task<bool> VerifyBcryptCompatAsync(
        string plainCode, string bcryptHash, CancellationToken cancellationToken)
    {
        await _cpuLimiter.EnterAsync("recovery_bcrypt", cancellationToken).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    foreach (var candidate in ExpandBcryptPlainCandidates(plainCode))
                    {
                        if (BCrypt.Net.BCrypt.Verify(candidate, bcryptHash))
                            return true;
                    }

                    return false;
                }
                catch
                {
                    return false;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _cpuLimiter.Exit("recovery_bcrypt", sw.Elapsed.TotalMilliseconds);
        }
    }

    private static IEnumerable<string> ExpandBcryptPlainCandidates(string plainCode)
    {
        var trimmed = plainCode.Trim();
        yield return trimmed;

        var noDash = trimmed.Replace("-", "", StringComparison.Ordinal);
        if (!string.Equals(noDash, trimmed, StringComparison.Ordinal))
            yield return noDash;

        var upper = noDash.ToUpperInvariant();
        if (!string.Equals(upper, noDash, StringComparison.Ordinal))
            yield return upper;

        // 32 位十六进制：补回分组连字符（录入时常省略）
        if (noDash.Length == 32)
        {
            var dashed = $"{noDash[..8]}-{noDash[8..16]}-{noDash[16..24]}-{noDash[24..32]}";
            if (!string.Equals(dashed, trimmed, StringComparison.Ordinal))
                yield return dashed;

            var dashedUpper = $"{upper[..8]}-{upper[8..16]}-{upper[16..24]}-{upper[24..32]}";
            if (!string.Equals(dashedUpper, dashed, StringComparison.Ordinal)
                && !string.Equals(dashedUpper, trimmed, StringComparison.Ordinal))
                yield return dashedUpper;
        }
    }

    private static string Normalize(string code)
        => code.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();

    private static byte[] Derive(string material) => SHA256.HashData(Encoding.UTF8.GetBytes("recovery:" + material));

    private static byte[] ComputeHmac(byte[] key, string normalizedPlain)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(normalizedPlain));
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static bool TrySplitVersioned(string value, out int version, out string payload)
    {
        version = 0;
        payload = "";
        if (!value.StartsWith('v')) return false;
        var colon = value.IndexOf(':');
        if (colon <= 1) return false;
        if (!int.TryParse(value.AsSpan(1, colon - 1), out version) || version <= 0)
            return false;
        payload = value[(colon + 1)..];
        return payload.Length > 0;
    }
}
