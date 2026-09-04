using Microsoft.Extensions.Logging;

namespace ChatApp.Server.Controllers;

/// <summary>
/// 附件隔离/回收审计事件（ACCOUNT-OPS-1）。EventId 段 4100-4199 为 Server 附件审计专用，
/// 稳定可检索；只记录 id 与决策码，不记录票据明文、签名 URL、用户名等敏感值。
/// 否定决策用 Warning 留痕越权尝试，状态转换用 Information 供回收链路对账。
/// </summary>
internal static partial class AttachmentAuditLog
{
    /// <summary>下载/签票授权被拒（401 匿名、403 越权、票据无效、扫描中）。</summary>
    [LoggerMessage(EventId = 4101, Level = LogLevel.Warning,
        Message = "附件下载授权被拒 AttachmentId={AttachmentId} Requester={Requester} Action={Action} Decision={Decision}")]
    public static partial void DownloadDenied(
        ILogger logger,
        string attachmentId,
        string requester,
        string action,
        string decision);

    /// <summary>预签被拒（MIME/大小不合规或配额超限）；此时尚无 attachmentId。</summary>
    [LoggerMessage(EventId = 4102, Level = LogLevel.Warning,
        Message = "附件预签被拒 UserId={UserId} Reason={Reason} Detail={Detail}")]
    public static partial void PresignDenied(
        ILogger logger,
        long userId,
        string reason,
        string? detail);

    /// <summary>下载短时票签发成功。</summary>
    [LoggerMessage(EventId = 4110, Level = LogLevel.Information,
        Message = "下载票已签发 AttachmentId={AttachmentId} UserId={UserId}")]
    public static partial void DownloadTicketIssued(
        ILogger logger,
        string attachmentId,
        long userId);

    /// <summary>附件确认已受理（进入扫描；绑定前不可被他人下载）。</summary>
    [LoggerMessage(EventId = 4111, Level = LogLevel.Information,
        Message = "附件确认已受理 AttachmentId={AttachmentId} OwnerId={OwnerId}")]
    public static partial void AttachmentConfirmed(
        ILogger logger,
        string attachmentId,
        long ownerId);

    /// <summary>附件被上传者放弃（进入回收：解绑 + blob 删除入队）。</summary>
    [LoggerMessage(EventId = 4112, Level = LogLevel.Information,
        Message = "附件已放弃 AttachmentId={AttachmentId} UserId={UserId}")]
    public static partial void AttachmentAbandoned(
        ILogger logger,
        string attachmentId,
        long userId);

    /// <summary>断点续传分块追加成功（状态转换，供续传链路对账）。</summary>
    [LoggerMessage(EventId = 4113, Level = LogLevel.Information,
        Message = "附件分块已追加 AttachmentId={AttachmentId} UserId={UserId} Offset={Offset} Received={Received}")]
    public static partial void ChunkAppended(
        ILogger logger,
        string attachmentId,
        long userId,
        long offset,
        long received);

    /// <summary>断点续传定稿完成（对象原子提升到最终路径，票转入 confirm 窗口）。</summary>
    [LoggerMessage(EventId = 4114, Level = LogLevel.Information,
        Message = "附件分块上传完成 AttachmentId={AttachmentId} UserId={UserId} SizeBytes={SizeBytes}")]
    public static partial void ChunkUploadCompleted(
        ILogger logger,
        string attachmentId,
        long userId,
        long sizeBytes);
}
