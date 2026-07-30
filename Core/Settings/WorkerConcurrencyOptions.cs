namespace Core.Settings;

/// <summary>
/// 后台 Worker 并发配置。
/// </summary>
public class WorkerConcurrencyOptions
{
    public const string SectionName = "WorkerConcurrency";

    /// <summary>全局并发预算：所有后台 Worker 同时执行的任务总数上限。</summary>
    public int GlobalMaxConcurrency { get; init; } = 16;

    /// <summary>通知分发 Worker 并发数。</summary>
    public int NotificationDispatch { get; init; } = 4;

    /// <summary>邮件分发 Worker 并发数。</summary>
    public int EmailDispatch { get; init; } = 4;

    /// <summary>附件扫描 Worker 并发数。</summary>
    public int AttachmentScan { get; init; } = 2;

    /// <summary>数据导出 Worker 并发数。</summary>
    public int DataExport { get; init; } = 2;
}
