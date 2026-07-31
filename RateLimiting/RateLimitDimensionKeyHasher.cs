using System.Security.Cryptography;
using System.Text;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace ChatApp.Server.RateLimiting;

/// <summary>将账户、设备和地址维度压缩为不可逆的固定长度 Redis key 片段。</summary>
public sealed class RateLimitDimensionKeyHasher
{
    private readonly byte[] _secret;

    public RateLimitDimensionKeyHasher(IOptions<SecurityOptions> security)
    {
        var material = security.Value.SecretEncryptionKey;
        // Development/Testing can run with the documented local fallback; the
        // production startup validator still requires SecretEncryptionKey.
        if (string.IsNullOrWhiteSpace(material))
            material = "chatapp-development-rate-limit-key";
        _secret = Encoding.UTF8.GetBytes(material);
    }

    public string Hash(string normalizedValue)
    {
        var digest = HMACSHA256.HashData(
            _secret,
            Encoding.UTF8.GetBytes(normalizedValue));
        return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }
}
