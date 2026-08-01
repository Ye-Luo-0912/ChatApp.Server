using System.Diagnostics.Metrics;

namespace Infrastructure.Services;

/// <summary>认证 / 密码 / 可信设备 / 导出相关指标（静态 Meter，无需改测试构造函数）。</summary>
public static class AuthSecurityMetrics
{
    private static readonly Meter AuthMeter = new("Infrastructure.Auth");
    private static readonly Meter PasswordMeter = new("Infrastructure.PasswordHashing");
    private static readonly Meter TrustedMeter = new("Infrastructure.TrustedDevice");
    private static readonly Meter ExportMeter = new("Infrastructure.DataExport");
    private static readonly Meter RiskMeter = new("Infrastructure.LoginRisk");

    private static readonly Counter<long> Logins =
        AuthMeter.CreateCounter<long>("auth.login", "requests", "登录结果（按 outcome 分标签）");
    private static readonly Counter<long> PasswordOps =
        PasswordMeter.CreateCounter<long>("password.hashing.ops", "ops", "密码哈希/校验操作");
    private static readonly Counter<long> PasswordOverloaded =
        PasswordMeter.CreateCounter<long>("password.hashing.overloaded", "ops", "闸门超时拒绝（503）");
    private static readonly Histogram<double> PasswordDuration =
        PasswordMeter.CreateHistogram<double>("password.hashing.duration", "ms", "密码哈希/校验耗时（p99）");
    private static readonly Histogram<double> PasswordWait =
        PasswordMeter.CreateHistogram<double>("password.hashing.wait", "ms", "闸门等待耗时");
    private static long _passwordInFlight;

    private static readonly Counter<long> TrustedOps =
        TrustedMeter.CreateCounter<long>("trusted_device.ops", "ops", "可信设备操作");
    private static readonly Counter<long> ExportJobs =
        ExportMeter.CreateCounter<long>("data_export.jobs", "jobs", "导出作业状态变更");
    private static readonly Histogram<double> ExportDuration =
        ExportMeter.CreateHistogram<double>("data_export.duration", "ms", "导出作业耗时");
    private static long _exportPending;

    private static readonly Counter<long> RiskSignals =
        RiskMeter.CreateCounter<long>("login_risk.signals", "events", "异步登录风险信号");
    private static readonly Counter<long> SessionChurnEvictions =
        AuthMeter.CreateCounter<long>("auth.session_churn_evictions", "sessions", "因用户会话上限淘汰的旧会话");
    private static readonly Counter<long> TokenL1Operations =
        AuthMeter.CreateCounter<long>("auth.token_l1", "ops", "访问令牌 L1 命中、未命中和驱逐");

    private static readonly Meter CleanupMeter = new("Infrastructure.AccountCleanup");
    private static readonly Counter<long> CleanupOps =
        CleanupMeter.CreateCounter<long>("account_cleanup.ops", "events", "账号清理 Saga 操作");

    private static readonly Counter<long> ExportBlobDeletes =
        ExportMeter.CreateCounter<long>("data_export.blob_delete", "ops", "导出 blob 删除结果");
    private static long _exportPendingDelete;

    private static readonly Meter AttachmentMeter = new("Infrastructure.Attachments");
    private static readonly Counter<long> AttachmentBlobDeletes =
        AttachmentMeter.CreateCounter<long>("attachment.blob_delete", "ops", "附件 blob 删除结果");
    private static readonly Counter<long> AttachmentScans =
        AttachmentMeter.CreateCounter<long>("attachment.scan", "ops", "附件内容扫描状态变迁");
    private static readonly Counter<long> AttachmentUploadReservations =
        AttachmentMeter.CreateCounter<long>("attachment.upload_reservation", "ops", "附件上传配额预留结果");
    private static long _attachmentPendingDelete;
    private static long _attachmentPendingScan;

    static AuthSecurityMetrics()
    {
        PasswordMeter.CreateObservableGauge(
            "password.hashing.in_flight",
            () => Volatile.Read(ref _passwordInFlight),
            "ops",
            "正在执行的密码哈希/校验数");
        ExportMeter.CreateObservableGauge(
            "data_export.pending",
            () => Volatile.Read(ref _exportPending),
            "jobs",
            "待处理/处理中导出作业数");
        ExportMeter.CreateObservableGauge(
            "data_export.pending_delete",
            () => Volatile.Read(ref _exportPendingDelete),
            "jobs",
            "blob 删除失败墓碑数（告警钩子）");
        AttachmentMeter.CreateObservableGauge(
            "attachment.pending_delete",
            () => Volatile.Read(ref _attachmentPendingDelete),
            "jobs",
            "附件 blob 删除墓碑数（告警钩子）");
        AttachmentMeter.CreateObservableGauge(
            "attachment.pending_scan",
            () => Volatile.Read(ref _attachmentPendingScan),
            "jobs",
            "附件内容扫描待处理作业数（告警钩子）");
    }

    public static void RecordLogin(string outcome)
        => Logins.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void BeginPasswordOp() => Interlocked.Increment(ref _passwordInFlight);

    public static void RecordPasswordWait(string op, double milliseconds)
        => PasswordWait.Record(milliseconds, new KeyValuePair<string, object?>("op", op));

    public static void RecordPasswordOverloaded(string op)
        => PasswordOverloaded.Add(1, new KeyValuePair<string, object?>("op", op));

    public static void EndPasswordOp(string op, double milliseconds)
    {
        Interlocked.Decrement(ref _passwordInFlight);
        PasswordOps.Add(1, new KeyValuePair<string, object?>("op", op));
        PasswordDuration.Record(milliseconds, new KeyValuePair<string, object?>("op", op));
    }

    public static void RecordTrusted(string op)
        => TrustedOps.Add(1, new KeyValuePair<string, object?>("op", op));

    public static void ExportEnqueued()
    {
        Interlocked.Increment(ref _exportPending);
        ExportJobs.Add(1, new KeyValuePair<string, object?>("status", "enqueued"));
    }

    public static void ExportFinished(string status, double? durationMs = null)
    {
        Interlocked.Decrement(ref _exportPending);
        ExportJobs.Add(1, new KeyValuePair<string, object?>("status", status));
        if (durationMs is { } ms)
            ExportDuration.Record(ms, new KeyValuePair<string, object?>("status", status));
    }

    public static void RecordRisk(string signal)
        => RiskSignals.Add(1, new KeyValuePair<string, object?>("signal", signal));

    public static void RecordSessionChurnEviction()
        => SessionChurnEvictions.Add(1);

    public static void RecordTokenL1(string operation)
        => TokenL1Operations.Add(1, new KeyValuePair<string, object?>("operation", operation));

    public static void RecordAccountCleanup(string outcome, int count = 1)
        => CleanupOps.Add(count, new KeyValuePair<string, object?>("outcome", outcome));

    public static void ExportBlobDelete(string outcome)
        => ExportBlobDeletes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void ExportPendingDeleteDelta(int delta)
        => Interlocked.Add(ref _exportPendingDelete, delta);

    public static void AttachmentBlobDelete(string outcome)
        => AttachmentBlobDeletes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void AttachmentScan(string outcome)
        => AttachmentScans.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void AttachmentUploadReservation(string outcome)
        => AttachmentUploadReservations.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void AttachmentPendingDeleteDelta(int delta)
        => Interlocked.Add(ref _attachmentPendingDelete, delta);

    public static void AttachmentPendingScanDelta(int delta)
        => Interlocked.Add(ref _attachmentPendingScan, delta);
}
