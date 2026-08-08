using System.Diagnostics;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Interfaces;
using Core.Models.Token;
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
    private const int StackNormalizedChars = 128;
    private const int StackUtf8Bytes = 256;
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
        // 分组便于人工录入：XXXXXXXX-XXXXXXXX-XXXXXXXX-XXXXXXXX
        return TokenBufferEncoding.CreateGroupedHex(EntropyBytes, groupBytes: 4);
    }

    public string Hash(string plainCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainCode);
        char[]? rentedChars = null;
        Span<char> normalized = plainCode.Length <= StackNormalizedChars
            ? stackalloc char[StackNormalizedChars]
            : (rentedChars = ArrayPool<char>.Shared.Rent(plainCode.Length)).AsSpan(0, plainCode.Length);
        try
        {
            var normalizedLength = NormalizeInto(plainCode, normalized);
            Span<byte> digest = stackalloc byte[32];
            ComputeHmac(
                _keysByVersion[_currentVersion],
                normalized[..normalizedLength],
                digest);
            return $"v{_currentVersion}:{TokenBufferEncoding.EncodeBase64Url(digest)}";
        }
        finally
        {
            if (rentedChars is not null)
                ArrayPool<char>.Shared.Return(rentedChars, clearArray: true);
        }
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

        Span<byte> expected = stackalloc byte[32];
        if (!TryDecodeBase64Url(payload, expected, out var expectedLength)
            || expectedLength != expected.Length)
            return false;

        Span<byte> actual = stackalloc byte[32];
        if (!TryComputeNormalizedHmac(key, plainCode, actual))
            return false;
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

    private static byte[] Derive(string material) => SHA256.HashData(Encoding.UTF8.GetBytes("recovery:" + material));

    private static int NormalizeInto(string code, Span<char> destination)
    {
        var start = 0;
        var end = code.Length;
        while (start < end && char.IsWhiteSpace(code[start]))
            start++;
        while (end > start && char.IsWhiteSpace(code[end - 1]))
            end--;

        var written = 0;
        for (var i = start; i < end; i++)
        {
            if (code[i] == '-')
                continue;
            if (written >= destination.Length)
                throw new ArgumentException("恢复码过长", nameof(code));
            destination[written++] = char.ToUpperInvariant(code[i]);
        }

        return written;
    }

    private static void ComputeHmac(
        byte[] key,
        ReadOnlySpan<char> normalizedPlain,
        Span<byte> destination)
    {
        var byteCount = Encoding.UTF8.GetByteCount(normalizedPlain);
        byte[]? rented = null;
        Span<byte> utf8 = byteCount <= StackUtf8Bytes
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(normalizedPlain, utf8);
            HMACSHA256.HashData(key, utf8[..written], destination);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static bool TryComputeNormalizedHmac(
        byte[] key,
        string plainCode,
        Span<byte> destination)
    {
        if (plainCode.Length <= StackNormalizedChars)
        {
            Span<char> normalized = stackalloc char[StackNormalizedChars];
            var length = NormalizeInto(plainCode, normalized);
            ComputeHmac(key, normalized[..length], destination);
            return true;
        }

        var rented = ArrayPool<char>.Shared.Rent(plainCode.Length);
        try
        {
            var length = NormalizeInto(plainCode, rented);
            ComputeHmac(key, rented.AsSpan(0, length), destination);
            return true;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented, clearArray: true);
        }
    }

    private static bool TryDecodeBase64Url(
        string value,
        Span<byte> destination,
        out int written)
    {
        written = 0;
        if (value.Length > 128)
            return false;

        Span<char> normalized = stackalloc char[128];
        var length = value.Length;
        for (var i = 0; i < length; i++)
        {
            normalized[i] = value[i] switch
            {
                '-' => '+',
                '_' => '/',
                _ => value[i],
            };
        }

        while (length % 4 != 0)
            normalized[length++] = '=';

        return Convert.TryFromBase64Chars(
            normalized[..length], destination, out written);
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
