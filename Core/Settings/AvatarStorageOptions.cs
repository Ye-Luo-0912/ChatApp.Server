namespace Core.Settings;

public sealed class AvatarStorageOptions
{
    public const string SectionName = "AvatarStorage";

    /// <summary>Local | S3</summary>
    public string Provider { get; set; } = "Local";

    public long MaxBytes { get; set; } = 2 * 1024 * 1024;

    public string[] AllowedContentTypes { get; set; } =
    [
        "image/jpeg",
        "image/png",
        "image/webp",
    ];

    /// <summary>本地落盘目录（容器内建议挂载卷）。</summary>
    public string LocalRootPath { get; set; } = "App_Data/avatars";

    /// <summary>对外访问前缀，例如 https://cdn.example.com/avatars</summary>
    public string PublicBaseUrl { get; set; } = "/static/avatars";

    /// <summary>上传票有效期。</summary>
    public int TicketMinutes { get; set; } = 15;

    /// <summary>用户名修改冷却天数。</summary>
    public int UserNameCooldownDays { get; set; } = 30;

    // S3 兼容（可选）
    public string? S3Bucket { get; set; }
    public string? S3Endpoint { get; set; }
    public string? S3AccessKey { get; set; }
    public string? S3SecretKey { get; set; }
    public string? S3Region { get; set; } = "us-east-1";
}

public sealed class ProfileOptions
{
    public const string SectionName = "Profile";
    public int UserNameCooldownDays { get; set; } = 30;
    public int UserNameMinLength { get; set; } = 3;
    public int UserNameMaxLength { get; set; } = 32;
}
