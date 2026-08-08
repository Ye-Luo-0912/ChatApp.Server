namespace Core.Models.Token;

/// <summary>设备凭据的生成、格式校验和摘要计算。</summary>
public static class DeviceCredentialHelper
{
    private const int CredentialBytes = 32;
    private const int MinEncodedLength = 32;
    private const int MaxEncodedLength = 128;

    public static string Create()
        => TokenBufferEncoding.CreateBase64Url(CredentialBytes);

    public static string ComputeHash(string credential)
        => TokenBufferEncoding.Sha256Utf8ToHex(credential);

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
