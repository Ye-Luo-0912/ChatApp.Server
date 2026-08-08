using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Core.Models.Token;

/// <summary>
/// 将客户端提供的原始设备 ID 稳定映射为 64 位指纹。
/// HTTP 与实时宿主共享此实现，避免一侧接受原始 ID、另一侧却按预哈希值解码。
/// </summary>
public static class DeviceIdHashHelper
{
    private const int StackUtf8Limit = 256;

    /// <summary>
    /// 对原始设备 ID 计算 SHA-256，并取摘要前 64 位作为紧凑指纹。
    /// <see langword="null"/> 或空字符串返回 <see langword="null"/>。
    /// 常规设备 ID 全程使用栈内存。
    /// </summary>
    public static ulong? Compute(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return null;

        var byteCount = Encoding.UTF8.GetByteCount(deviceId);
        byte[]? rented = null;
        Span<byte> utf8 = byteCount <= StackUtf8Limit
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);

        try
        {
            var written = Encoding.UTF8.GetBytes(deviceId, utf8);
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(utf8[..written], digest);
            return BinaryPrimitives.ReadUInt64BigEndian(digest);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// 验证客户端提供的设备 ID 是否与存储的哈希值匹配。
    /// </summary>
    public static bool Verify(string? deviceId, ulong? storedHash)
        => storedHash.HasValue && Compute(deviceId) == storedHash.Value;
}
