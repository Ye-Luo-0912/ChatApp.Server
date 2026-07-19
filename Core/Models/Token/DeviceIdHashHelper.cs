using System.Buffers.Binary;

namespace Core.Models.Token;

/// <summary>
/// 设备指纹哈希工具——将 SHA-256 Base64url 编码的设备 ID 折叠为 64 位整数。
/// <para>
/// 算法：SHA-256 全 32 字节分为 4 组 × 8 字节，逐组 XOR 折叠为单个 <see cref="ulong"/>（大端序）。
/// 结果以紧凑数字形式存入 <see cref="AccessTokenData"/>，最大限度降低热路径存储开销。
/// </para>
/// <para>
/// 本类无外部依赖，可在 HTTP 服务、TCP 服务等多个宿主中共享使用，确保哈希行为一致。
/// </para>
/// </summary>
public static class DeviceIdHashHelper
{
    /// <summary>
    /// 将设备 ID（SHA-256 Base64url，无填充，43-44 字符）计算为 64 位哈希值。
    /// 输入为 <see langword="null"/> 或格式不合法时返回 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 实现全程使用栈分配，不产生堆内存分配。
    /// </remarks>
    public static ulong? Compute(string? deviceId)
    {
        if (deviceId is null) return null;

        // SHA-256 经 Base64url 无填充编码后恒为 43 字符，加填充为 44 字符
        var srcLen = deviceId.Length;
        if (srcLen is < 43 or > 44) return null;

        // Base64url → Base64 标准（栈分配，避免 string 堆分配）
        var mod       = srcLen % 4;
        var padCount  = mod == 0 ? 0 : 4 - mod;
        var b64Length = srcLen + padCount;

        Span<char> b64Chars = stackalloc char[b64Length];
        for (var i = 0; i < srcLen; i++)
            b64Chars[i] = deviceId[i] switch { '-' => '+', '_' => '/', var c => c };
        b64Chars[srcLen..].Fill('=');

        // SHA-256 输出恰好 32 字节 → 栈分配
        Span<byte> decoded = stackalloc byte[32];
        if (!Convert.TryFromBase64Chars(b64Chars, decoded, out var written) || written < 32)
            return null;

        // XOR 折叠：4 组 × 8 字节，充分利用全部 256 位熵
        var h = BinaryPrimitives.ReadUInt64BigEndian(decoded[..8]);
        h    ^= BinaryPrimitives.ReadUInt64BigEndian(decoded[8..16]);
        h    ^= BinaryPrimitives.ReadUInt64BigEndian(decoded[16..24]);
        h    ^= BinaryPrimitives.ReadUInt64BigEndian(decoded[24..32]);
        return h;
    }

    /// <summary>
    /// 验证客户端提供的设备 ID 是否与存储的哈希值匹配。
    /// <paramref name="storedHash"/> 为 <see langword="null"/> 时始终返回 <see langword="false"/>。
    /// </summary>
    public static bool Verify(string? deviceId, ulong? storedHash)
        => storedHash.HasValue && Compute(deviceId) == storedHash.Value;
}
