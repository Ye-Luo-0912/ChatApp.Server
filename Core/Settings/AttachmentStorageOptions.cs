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

    /// <summary>每用户允许同时处于 Ticketed/Uploaded/Scanning 的对象数。</summary>
    public int MaxUnconfirmedObjectsPerUser { get; set; } = 20;

    /// <summary>
    /// 每用户附件存储配额。包含 Ticketed/Uploaded/Scanning/Confirmed/Bound，
    /// Ticketed 按单对象 <see cref="MaxBytes"/> 预留；上传后收敛为实际大小，
    /// 防止预签 PUT 以虚假的小 Content-Length 绕过配额。
    /// </summary>
    public long MaxStorageBytesPerUser { get; set; } = 5L * 1024 * 1024 * 1024;

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

    /// <summary>
    /// 鉴权下载短时票有效期（分钟）。客户端先 POST ticket 再带 ?ticket= 下载；单次消费。
    /// </summary>
    public int DownloadTicketMinutes { get; set; } = 2;

    /// <summary>附件 blob 删除墓碑最大重试次数（超过后转为 DeadLetter，等待人工处置）。</summary>
    public int MaxDeleteAttempts { get; set; } = 20;

    /// <summary>删除重试基础退避秒数（指数：base * 2^attempt，上限 1h）。</summary>
    public int DeleteBackoffSeconds { get; set; } = 30;

    /// <summary>删除 Worker 每轮处理条数。</summary>
    public int DeleteBatchSize { get; set; } = 50;

    /// <summary>内容扫描最大重试次数（瞬时失败）；耗尽后 Rejected。</summary>
    public int MaxScanAttempts { get; set; } = 10;

    /// <summary>DenyList（开发兜底）或 ClamAV（生产推荐）。</summary>
    public string ScannerProvider { get; set; } = "DenyList";
    public string? ClamAvHost { get; set; }
    public int ClamAvPort { get; set; } = 3310;
    public int ClamAvTimeoutMilliseconds { get; set; } = 30_000;
    public string ClamAvEngineVersion { get; set; } = "configured-at-runtime";

    /// <summary>扫描重试基础退避秒数（指数：base * 2^attempt，上限 1h）。</summary>
    public int ScanBackoffSeconds { get; set; } = 15;

    /// <summary>扫描 Worker 每轮处理条数。</summary>
    public int ScanBatchSize { get; set; } = 20;

    /// <summary>确认 Saga 每轮最多领取数量。</summary>
    public int ConfirmBatchSize { get; set; } = 20;

    /// <summary>确认 Saga 租约秒数。</summary>
    public int ConfirmLeaseSeconds { get; set; } = 120;

    /// <summary>确认 Saga 最大重试次数。</summary>
    public int MaxConfirmAttempts { get; set; } = 10;

    /// <summary>扫描结果投影租约秒数；长于单次 Realtime/S3 操作即可。</summary>
    public int ProjectionLeaseSeconds { get; set; } = 120;

    /// <summary>扫描审计保留天数；独立于短期扫描作业清理周期。</summary>
    public int ScanAuditRetentionDays { get; set; } = 90;

    /// <summary>扫描临时目录的硬字节上限（包含进程重启后残留文件）。</summary>
    public long ScanStagingMaxBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>扫描 Worker 可同时保留的临时文件字节数。</summary>
    public long ScanMaxConcurrentBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>扫描临时文件目录；生产容器应映射到有明确配额的 tmpfs/卷。</summary>
    public string ScanStagingRoot { get; set; } = "/tmp/chatapp-scan";

    /// <summary>部署层 tmpfs 预算，仅用于启动配置/运维校验。</summary>
    public long TmpfsSizeBytes { get; set; } = 128L * 1024 * 1024;

    /// <summary>压缩归档最多允许的条目数/声明解压大小，防止 zip bomb。</summary>
    public int ArchiveMaxEntries { get; set; } = 10_000;
    public long ArchiveMaxUncompressedBytes { get; set; } = 250 * 1024 * 1024;
    public int ArchiveMaxPathDepth { get; set; } = 8;
    public int ArchiveMaxNestingDepth { get; set; } = 3;

    /// <summary>
    /// 运维：Scanning 超过该分钟数视为卡住（元数据或扫描作业）。
    /// </summary>
    public int StuckScanningMinutes { get; set; } = 30;

    /// <summary>
    /// 运维：删除墓碑 AttemptCount ≥ 该阈值视为高失败风险样例。
    /// </summary>
    public int OpsHighDeleteAttemptThreshold { get; set; } = 5;

    /// <summary>运维查询最差样例条数上限（硬夹到 1–20）。</summary>
    public int OpsSampleLimit { get; set; } = 20;

    /// <summary>
    /// 是否启用过期未绑定 Ticketed/Confirmed 自动 Abandoned 并入队 blob 删除。
    /// </summary>
    public bool AbandonedUnboundEnabled { get; set; } = true;

    /// <summary>
    /// 未绑定 Ticketed/Confirmed 超过该分钟数则 Abandoned；已 Abandoned 的
    /// 候选会继续用于补建删除墓碑；≤0 时回退为 max(30, TicketMinutes*4)。
    /// </summary>
    public int AbandonedUnboundAgeMinutes { get; set; }

    /// <summary>年龄清扫每轮最多放弃条数。</summary>
    public int AbandonedUnboundBatchSize { get; set; } = 50;

    /// <summary>SSE-S3 或 SSE-KMS；Provider=S3 时必须启用。</summary>
    public string S3SseMode { get; set; } = "SSE-S3";

    /// <summary>Provider=S3 且 S3SseMode=SSE-KMS 时的 KMS key id/ARN。</summary>
    public string? S3KmsKeyId { get; set; }
    public string? S3Bucket { get; set; }
    public string? S3Endpoint { get; set; }
    public bool S3ForcePathStyle { get; set; }
    // 旧字段保留用于兼容已有配置，但不再被实现读取。
    public string? S3AccessKey { get; set; }
    public string? S3SecretKey { get; set; }
    public string? S3Region { get; set; } = "us-east-1";
}
