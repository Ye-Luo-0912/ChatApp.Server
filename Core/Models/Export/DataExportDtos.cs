namespace Core.Models.Export;

public sealed record DataExportStatusDto(
    string JobId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadyAt,
    DateTimeOffset? ExpiresAt,
    string? ErrorCode);

/// <summary>导出下载稳定错误码。</summary>
public static class DataExportDownloadErrors
{
    public const string JobNotFound = "job_not_found";
    public const string DownloadConsumed = "download_consumed";
    public const string Expired = "expired";
    public const string NotReady = "not_ready";
    public const string BlobMissing = "blob_missing";
    public const string Cancelled = "cancelled";
}

/// <summary>导出作业对客户端公开的稳定错误码。</summary>
public static class DataExportJobErrors
{
    public const string ExportFailed = "export_failed";
    public const string UserNotFound = "user_not_found";
    public const string LeaseLost = "lease_lost";
    public const string ChatSourceFailed = "chat_source_failed";
    public const string Cancelled = "cancelled";

    public static string MapPublicCode(Exception ex) => ex switch
    {
        InvalidOperationException ioe when ioe.Message.Contains("用户不存在", StringComparison.Ordinal)
            => UserNotFound,
        InvalidOperationException ioe when ioe.Message.Contains("租约", StringComparison.Ordinal)
            => LeaseLost,
        InvalidOperationException ioe when ioe.Message.Contains("Realtime 历史查询失败", StringComparison.Ordinal)
            => ChatSourceFailed,
        _ => ExportFailed,
    };

    public static string? ToPublicErrorCode(string status, string? stored)
    {
        if (!string.Equals(status, DataExportJobStatus.Failed, StringComparison.Ordinal)
            && !string.Equals(status, DataExportJobStatus.PendingDelete, StringComparison.Ordinal)
            && !string.Equals(status, DataExportJobStatus.DeleteDeadLetter, StringComparison.Ordinal)
            && !string.Equals(status, DataExportJobStatus.ConsumedPendingDelete, StringComparison.Ordinal)
            && !string.Equals(status, DataExportJobStatus.Cancelled, StringComparison.Ordinal)
            && !string.Equals(status, DataExportJobStatus.CancelRequested, StringComparison.Ordinal))
            return null;
        if (string.IsNullOrWhiteSpace(stored))
            return ExportFailed;
        return stored switch
        {
        ExportFailed or UserNotFound or LeaseLost or ChatSourceFailed or Cancelled => stored,
            _ => ExportFailed,
        };
    }
}
