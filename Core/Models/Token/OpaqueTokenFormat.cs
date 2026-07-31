namespace Core.Models.Token;

/// <summary>
/// Opaque access/refresh token 的固定 Base64url 线格式。
/// 令牌来自 HTTP 边界，必须在构造 Redis key 或执行哈希前完成校验。
/// </summary>
public static class OpaqueTokenFormat
{
    /// <summary>访问令牌由 <c>Generate(16)</c> 生成。</summary>
    public const int AccessTokenByteLength = 16;

    public static bool IsAccessToken(ReadOnlySpan<char> token)
        => IsBase64UrlToken(token, AccessTokenByteLength);

    public static bool IsRefreshToken(ReadOnlySpan<char> token, int byteLength)
        => IsBase64UrlToken(token, byteLength);

    /// <summary>无填充 Base64url 的字符长度。</summary>
    public static int GetBase64UrlLength(int byteLength)
    {
        if (byteLength <= 0)
            return 0;

        return checked((byteLength * 8 + 5) / 6);
    }

    public static bool IsBase64UrlToken(ReadOnlySpan<char> token, int byteLength)
    {
        if (byteLength <= 0 || token.Length != GetBase64UrlLength(byteLength))
            return false;

        foreach (var c in token)
        {
            var alphaNumeric = (c is >= 'A' and <= 'Z')
                               || (c is >= 'a' and <= 'z')
                               || (c is >= '0' and <= '9');
            if (!alphaNumeric && c is not '-' and not '_')
                return false;
        }

        return true;
    }
}
