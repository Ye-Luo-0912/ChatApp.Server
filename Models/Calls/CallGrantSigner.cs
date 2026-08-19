using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ChatApp.Server.Models.Calls;

/// <summary>
/// 签发 call grant 的 HMAC-SHA256 签名器。
/// <para>
/// 签名覆盖排序后的规范载荷 <c>CallId|CallerUserId|CalleeUserId|ExpiresAtMs|Nonce</c>，
/// 以 <c>JwtSettings.Secret</c> 为共享密钥，输出标准 Base64。
/// Realtime 侧 <c>SignedCallGrantVerifier</c> 使用同一canonical 载荷与同一密钥校验。
/// </para>
/// </summary>
public static class CallGrantSigner
{
    /// <summary>规范载荷字段分隔符（字段均为受限字符集，不含该分隔符）。</summary>
    internal const char Separator = '|';

    /// <summary>计算 grant 的 HMAC-SHA256 签名（标准 Base64）。</summary>
    public static string Sign(string secret, CallGrantResponse grant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(grant);

        var payload = BuildCanonicalPayload(grant);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(digest);
    }

    /// <summary>构造与 Realtime 校验端完全一致的规范载荷。</summary>
    public static string BuildCanonicalPayload(CallGrantResponse grant)
        => string.Concat(
            grant.CallId, Separator,
            grant.CallerUserId.ToString(CultureInfo.InvariantCulture), Separator,
            grant.CalleeUserId.ToString(CultureInfo.InvariantCulture), Separator,
            grant.ExpiresAtMs.ToString(CultureInfo.InvariantCulture), Separator,
            grant.Nonce);
}