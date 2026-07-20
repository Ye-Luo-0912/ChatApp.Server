namespace Core.Settings;

/// <summary>敏感字段加密等安全配置。</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// AES-GCM 密钥材料（任意字符串，经 SHA-256 派生 32 字节）。
    /// 生产环境必须配置，勿依赖 JWT Secret。
    /// </summary>
    public string SecretEncryptionKey { get; set; } = string.Empty;

    /// <summary>密钥版本，轮换时可并存解密旧密文。</summary>
    public int KeyVersion { get; set; } = 1;

    /// <summary>上一版密钥材料（可选，用于轮换过渡期解密）。</summary>
    public string? PreviousSecretEncryptionKey { get; set; }

    /// <summary>上一版密钥版本号。</summary>
    public int? PreviousKeyVersion { get; set; }
}
