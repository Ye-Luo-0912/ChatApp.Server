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

    /// <summary>附件扫描结果投影 Worker 并发数。</summary>
    public int AttachmentProjection { get; init; } = 4;

    /// <summary>附件确认 Saga Worker 并发数。</summary>
    public int AttachmentConfirm { get; init; } = 2;

    /// <summary>附件 blob 删除 Worker 并发数。</summary>
    public int AttachmentBlobDelete { get; init; } = 2;

    /// <summary>账号注销 Worker 并发数。</summary>
    public int AccountDeletion { get; init; } = 1;

    /// <summary>账号注销进入 DLQ 前允许的持久尝试次数。</summary>
    public int AccountDeletionMaxAttempts { get; init; } = 5;

    /// <summary>审核会话撤销 Worker 并发数。</summary>
    public int ModerationRevocation { get; init; } = 2;

    /// <summary>数据导出 Worker 并发数。</summary>
    public int DataExport { get; init; } = 2;
    /// <summary>头像 Finalization Saga Worker 并发数。</summary>
    public int AvatarFinalization { get; init; } = 2;
    public int SecurityAudit { get; init; } = 1;
    public int SecurityRevocation { get; init; } = 2;
    /// <summary>登录风险 GeoIP/历史分析 Worker 并发数。</summary>
    public int LoginRiskAnalysis { get; init; } = 2;
}
