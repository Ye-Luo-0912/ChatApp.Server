namespace Core.Settings;

public sealed class DataExportStorageOptions
{
    public const string SectionName = "DataExport";

    /// <summary>Local | S3。生产多实例应使用 S3。</summary>
    public string Provider { get; set; } = "Local";

    /// <summary>最终对象和 lease-scoped 中间文件使用的持久目录；容器应挂载有容量配额的卷。</summary>
    public string LocalRootPath { get; set; } = "App_Data/exports";

    public int JobTtlHours { get; set; } = 24;
    public int LeaseSeconds { get; set; } = 120;
    public int PollIntervalMilliseconds { get; set; } = 2000;
    /// <summary>过期作业与 blob 清理周期（分钟）。</summary>
    public int CleanupIntervalMinutes { get; set; } = 15;

    /// <summary>PendingDelete 墓碑最大重试次数（超过后仍保留行并继续打点告警）。</summary>
    public int MaxBlobDeleteAttempts { get; set; } = 20;

    /// <summary>
    /// 本地落盘是否 AES-GCM 加密（默认 true）。
    /// 使用 <c>Security:SecretEncryptionKey</c>；关闭仅用于调试。
    /// 格式：CAE3 分块 AES-GCM + 认证 EOF（流式读写）；仍可读遗留 CAE2 / CAE1 / 明文。
    /// </summary>
    public bool EncryptAtRest { get; set; } = true;

    /// <summary>CAE3 明文分块大小（字节）；默认 64KiB。</summary>
    public int EncryptChunkBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// 生产切 S3 时：关闭本地信封加密（EncryptAtRest=false），改用桶级 SSE-S3 或 SSE-KMS；
    /// blob store 实现应流式 PutObject/GetObject，勿缓冲整对象。
    /// </summary>
    public string S3SseMode { get; set; } = "SSE-S3";
    public string? S3Bucket { get; set; }
    public string? S3Endpoint { get; set; }
    public bool S3ForcePathStyle { get; set; }
    public string? S3Region { get; set; } = "us-east-1";
    public string? S3KmsKeyId { get; set; }
    // 旧配置字段保留兼容，但 S3 实现不读取静态 access key/secret。
    public string? S3AccessKey { get; set; }
    public string? S3SecretKey { get; set; }

    /// <summary>
    /// 可选：覆盖 MessageEvidence 的 Realtime Postgres 连接串，专供导出直读 messages。
    /// 为空时回退 MessageEvidence:RealtimeConnectionString，再回退 NATS 历史查询。
    /// </summary>
    public string? RealtimeConnectionString { get; set; }

    /// <summary>是否在导出包中包含聊天正文/回执/附件清单（默认 true）。</summary>
    public bool IncludeChatContent { get; set; } = true;

    /// <summary>直连 Postgres 时每页条数（默认 200，上限 500）。</summary>
    public int ChatExportPageSize { get; set; } = 200;

    /// <summary>单次导出消息上限，防止异常大账号拖垮 Worker（默认 200000）。</summary>
    public int ChatExportMaxMessages { get; set; } = 200_000;

    /// <summary>附件 URL 去重集合上限；超出后停止扫描并在 chatExport 中注明。</summary>
    public int ChatExportMaxAttachmentUrls { get; set; } = 50_000;

    /// <summary>正文超过该字符数时跳过 http URL 扫描（仍解析 JSON attachments）。</summary>
    public int ChatExportUrlScanMaxContentChars { get; set; } = 64 * 1024;
}
