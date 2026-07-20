using System.Security.Cryptography;
using System.Text;
using Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services.Auth;

public interface IMfaSecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedOrPlain);
}

/// <summary>AES-GCM 保护 TOTP 密钥；支持密钥版本与轮换过渡期解密。</summary>
public sealed class AesGcmMfaSecretProtector : IMfaSecretProtector
{
    private readonly Dictionary<int, byte[]> _keysByVersion;
    private readonly int _currentVersion;

    public AesGcmMfaSecretProtector(
        IOptions<SecurityOptions> security,
        IOptions<JwtSettings> jwt,
        IHostEnvironment env,
        ILogger<AesGcmMfaSecretProtector> logger)
    {
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
                ? "dev-only-mfa-encryption-key-change-me"
                : jwt.Value.Secret;
            logger.LogWarning("Security:SecretEncryptionKey 未配置，已临时回退（仅 Development/Testing）");
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

    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_keysByVersion[_currentVersion], 16);
        aes.Encrypt(nonce, plain, cipher, tag);

        var payload = new byte[12 + 16 + cipher.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, 12);
        Buffer.BlockCopy(tag, 0, payload, 12, 16);
        Buffer.BlockCopy(cipher, 0, payload, 28, cipher.Length);
        return $"v{_currentVersion}:" + Convert.ToBase64String(payload);
    }

    public string Unprotect(string protectedOrPlain)
    {
        if (string.IsNullOrWhiteSpace(protectedOrPlain))
            return protectedOrPlain;

        if (!TrySplitVersioned(protectedOrPlain, out var version, out var payloadB64))
            return protectedOrPlain; // legacy plaintext

        if (!_keysByVersion.TryGetValue(version, out var key))
            throw new CryptographicException($"Unknown MFA secret key version {version}");

        var payload = Convert.FromBase64String(payloadB64);
        if (payload.Length < 28)
            throw new CryptographicException("Invalid MFA secret payload");

        var nonce = payload.AsSpan(0, 12);
        var tag = payload.AsSpan(12, 16);
        var cipher = payload.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] Derive(string material) => SHA256.HashData(Encoding.UTF8.GetBytes(material));

    private static bool TrySplitVersioned(string value, out int version, out string payloadB64)
    {
        version = 0;
        payloadB64 = "";
        if (!value.StartsWith('v')) return false;
        var colon = value.IndexOf(':');
        if (colon <= 1) return false;
        if (!int.TryParse(value.AsSpan(1, colon - 1), out version) || version <= 0)
            return false;
        payloadB64 = value[(colon + 1)..];
        return payloadB64.Length > 0;
    }
}
