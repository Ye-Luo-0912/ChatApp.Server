using System.Security.Cryptography;
using System.Text;

namespace Core.Caching;

/// <summary>
/// 令牌哈希工具，将原始 token 字符串转换为 URL 安全的 Base64(SHA-256) 摘要。
/// <para>
/// 安全原因：不应直接把原始令牌存入 Redis key，避免 key 泄漏等价于 token 泄漏。
/// 对 token 做单向哈希后，即使 Redis key 被枚举也无法还原原始令牌。
/// </para>
/// <para>
/// 使用场景：生成 AccessToken / RefreshToken 的缓存 key，与 <see cref="CacheKeyBuilder"/> 配合。
/// </para>
/// </summary>
public static class TokenHasher
{
    /// <summary>
    /// 计算 token 的 SHA-256 摘要，返回 URL 安全的 Base64 字符串（无填充）。
    /// </summary>
    /// <param name="token">原始令牌字符串。</param>
    /// <returns>43 个字符的 URL-safe Base64 字符串。</returns>
    /// <exception cref="ArgumentException">token 为空时抛出。</exception>
    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        Span<byte> hashBytes = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(token), hashBytes);

        // TrimEnd('=') 去掉填充；替换 +/为 -_ 保证 key 中不含特殊字符
        return Convert.ToBase64String(hashBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// 构造令牌对应的缓存 key：{prefix}token:{hash}
    /// </summary>
    public static string BuildCacheKey(string prefix, string token)
        => CacheKeyBuilder.WithDomain(prefix, "token", Hash(token));
}
