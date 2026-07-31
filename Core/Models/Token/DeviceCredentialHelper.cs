using System.Security.Cryptography;
using System.Text;

namespace Core.Models.Token;

/// <summary>设备凭据的生成、格式校验和摘要计算。</summary>
public static class DeviceCredentialHelper
{
    private const int CredentialBytes = 32;
    private const int MinEncodedLength = 32;
    private const int MaxEncodedLength = 128;

    public static string Create()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(CredentialBytes))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string ComputeHash(string credential)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    public static bool IsValid(string? credential)
    {
        if (credential is null || credential.Length is < MinEncodedLength or > MaxEncodedLength)
            return false;

        foreach (var ch in credential)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
                continue;
            return false;
        }

        return true;
    }
}
