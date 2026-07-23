namespace Core.Settings;

/// <summary>
/// 正式附件对象存储。私有媒体默认不经静态文件公开；聊天侧用
/// <c>/api/attachments/{id}/download</c> 鉴权下载。S3 返回短时签名 GET。
/// </summary>
public sealed class AttachmentStorageOptions
{
    public const string SectionName = "AttachmentStorage";

    /// <summary>Local | S3</summary>
    public string Provider { get; set; } = "Local";

    public long MaxBytes { get; set; } = 25 * 1024 * 1024;

    public string[] AllowedContentTypes { get; set; } =
    [
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif",
        "application/pdf",
        "audio/mpeg",
        "audio/ogg",
        "video/mp4",
        "application/octet-stream",
    ];

    /// <summary>本地落盘目录（容器内建议挂载卷）。</summary>
    public string LocalRootPath { get; set; } = "App_Data/attachments";

    /// <summary>
    /// 遗留公开前缀（仅当 <see cref="UsePublicStatic"/> 为 true 时挂载静态文件）。
    /// 聊天 API 不再返回该 URL；请使用 downloadPath。
    /// </summary>
    public string PublicBaseUrl { get; set; } = "/static/attachments";

    /// <summary>
    /// 是否用 UseStaticFiles 公开 LocalRootPath。默认 false（私有附件必须走鉴权下载）。
    /// </summary>
    public bool UsePublicStatic { get; set; }

    /// <summary>上传票有效期（分钟）。</summary>
    public int TicketMinutes { get; set; } = 15;

    /// <summary>S3 签名下载 URL 有效期（分钟）。</summary>
    public int SignedDownloadMinutes { get; set; } = 5;

    /// <summary>附件 blob 删除墓碑最大重试次数（超过后仍 Pending 并打点告警）。</summary>
    public int MaxDeleteAttempts { get; set; } = 20;

    /// <summary>删除重试基础退避秒数（指数：base * 2^attempt，上限 1h）。</summary>
    public int DeleteBackoffSeconds { get; set; } = 30;

    /// <summary>删除 Worker 每轮处理条数。</summary>
    public int DeleteBatchSize { get; set; } = 50;

    // S3 兼容（可选）
    public string? S3Bucket { get; set; }
    public string? S3Endpoint { get; set; }
    public string? S3AccessKey { get; set; }
    public string? S3SecretKey { get; set; }
    public string? S3Region { get; set; } = "us-east-1";
}
