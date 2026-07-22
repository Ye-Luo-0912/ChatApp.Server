namespace Core.Settings;

public sealed class DataExportStorageOptions
{
    public const string SectionName = "DataExport";

    public string LocalRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "chatapp-exports");
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
    /// </summary>
    public bool EncryptAtRest { get; set; } = true;
}
