using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Core.Models.Token;

/// <summary>
/// Small-buffer encoders used at token and ticket boundaries.
///
/// The returned string is the intentional result allocation. Intermediate
/// byte/character arrays stay on the stack for normal sizes and use the shared
/// pools only when a caller supplies an unusually large value.
/// </summary>
public static class TokenBufferEncoding
{
    private const int StackByteLimit = 256;
    private const int StackCharLimit = 512;
    private static ReadOnlySpan<char> HexAlphabet => "0123456789ABCDEF";

    public static string CreateBase64Url(int byteLength)
    {
        if (byteLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(byteLength));

        byte[]? rented = null;
        Span<byte> bytes = byteLength <= StackByteLimit
            ? stackalloc byte[byteLength]
            : (rented = ArrayPool<byte>.Shared.Rent(byteLength)).AsSpan(0, byteLength);

        try
        {
            RandomNumberGenerator.Fill(bytes);
            return EncodeBase64Url(bytes);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static string CreateHex(int byteLength)
    {
        if (byteLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(byteLength));

        byte[]? rented = null;
        Span<byte> bytes = byteLength <= StackByteLimit
            ? stackalloc byte[byteLength]
            : (rented = ArrayPool<byte>.Shared.Rent(byteLength)).AsSpan(0, byteLength);

        try
        {
            RandomNumberGenerator.Fill(bytes);
            return EncodeHex(bytes);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Creates a grouped hexadecimal value with one result allocation. The
    /// random bytes and formatted characters stay on the stack for the small
    /// security-token sizes used by the application; larger callers use pools.
    /// </summary>
    public static string CreateGroupedHex(
        int byteLength,
        int groupBytes,
        char separator = '-')
    {
        if (byteLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(byteLength));
        if (groupBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(groupBytes));

        var outputLength = checked(byteLength * 2 + Math.Max(0, (byteLength - 1) / groupBytes));
        byte[]? rentedBytes = null;
        char[]? rentedChars = null;
        Span<byte> bytes = byteLength <= StackByteLimit
            ? stackalloc byte[byteLength]
            : (rentedBytes = ArrayPool<byte>.Shared.Rent(byteLength)).AsSpan(0, byteLength);
        Span<char> output = outputLength <= StackCharLimit
            ? stackalloc char[outputLength]
            : (rentedChars = ArrayPool<char>.Shared.Rent(outputLength)).AsSpan(0, outputLength);

        try
        {
            RandomNumberGenerator.Fill(bytes);
            var written = 0;
            for (var i = 0; i < byteLength; i++)
            {
                if (i > 0 && i % groupBytes == 0)
                    output[written++] = separator;

                var value = bytes[i];
                output[written++] = HexAlphabet[value >> 4];
                output[written++] = HexAlphabet[value & 0x0F];
            }

            return new string(output[..written]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (rentedBytes is not null)
                ArrayPool<byte>.Shared.Return(rentedBytes, clearArray: false);
            if (rentedChars is not null)
                ArrayPool<char>.Shared.Return(rentedChars, clearArray: false);
        }
    }

    public static string Sha256Utf8ToHex(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Span<byte> digest = stackalloc byte[32];
        FillSha256Utf8(value, digest);
        return EncodeHex(digest);
    }

    public static string Sha256Utf8ToBase64Url(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Span<byte> digest = stackalloc byte[32];
        FillSha256Utf8(value, digest);
        return EncodeBase64Url(digest);
    }

    public static string EncodeHex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(bytes);

    public static string EncodeBase64Url(ReadOnlySpan<byte> bytes)
    {
        var paddedLength = checked(((bytes.Length + 2) / 3) * 4);
        char[]? rented = null;
        Span<char> encoded = paddedLength <= StackCharLimit
            ? stackalloc char[paddedLength]
            : (rented = ArrayPool<char>.Shared.Rent(paddedLength)).AsSpan(0, paddedLength);

        try
        {
            if (!Convert.TryToBase64Chars(bytes, encoded, out var written))
                throw new InvalidOperationException("Base64 缓冲区长度不足");

            while (written > 0 && encoded[written - 1] == '=')
                written--;

            for (var i = 0; i < written; i++)
            {
                encoded[i] = encoded[i] switch
                {
                    '+' => '-',
                    '/' => '_',
                    _ => encoded[i],
                };
            }

            return new string(encoded[..written]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static void FillSha256Utf8(string value, Span<byte> digest)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        byte[]? rented = null;
        Span<byte> utf8 = byteCount <= StackByteLimit
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);

        try
        {
            var written = Encoding.UTF8.GetBytes(value, utf8);
            SHA256.HashData(utf8[..written], digest);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
