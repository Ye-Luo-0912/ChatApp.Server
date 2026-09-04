using Core.Models.Attachment;
using Core.Models.Auth;

namespace Core.Interfaces;

/// <summary>正式附件上传、确认与鉴权下载的应用边界。</summary>
public interface IAttachmentService
{
    Task<AttachmentPresignResult> PresignAsync(
        long userId,
        AttachmentPresignRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>整包上传。成功时返回实际接收字节数（供响应体 received 字段）。</summary>
    Task<(AuthOperationResult Result, long ReceivedBytes)> UploadAsync(
        long userId,
        string ticket,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 断点续传分块追加：offset 须等于服务端权威已接收字节数；票在续传期间保持有效，
    /// 累计达到票声明长度时服务端定稿（哈希 + 原子提升），Completed=true。
    /// </summary>
    Task<(bool Ok, bool Completed, long Received, string? AttachmentId, string? Error)> AppendUploadAsync(
        long userId,
        string ticket,
        long offset,
        Stream chunk,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>断点续传进度探针：服务端权威已接收字节数。只读。</summary>
    Task<(bool Ok, long Received, string? Error)> GetUploadProgressAsync(
        long userId,
        string ticket,
        CancellationToken cancellationToken = default);

    Task<(AuthOperationResult Result, ConfirmAttachmentResponse? Body)> ConfirmAsync(
        long userId,
        ConfirmAttachmentRequest request,
        CancellationToken cancellationToken = default);

    Task<AttachmentLifecycleStatusDto?> GetStatusAsync(
        long userId,
        string attachmentId,
        CancellationToken cancellationToken = default);

    Task<(AttachmentDownloadDecision Decision, AttachmentDownloadAccess? Access)> AuthorizeDownloadAsync(
        long userId,
        string attachmentId,
        CancellationToken cancellationToken = default);

    Task<(AttachmentDownloadDecision Decision, AttachmentDownloadTicketResponse? Body)> IssueDownloadTicketAsync(
        long userId,
        string attachmentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 校验绑定关系后原子消费短时下载票；成功打开即视为消费。
    /// </summary>
    Task<(AttachmentDownloadDecision Decision, AttachmentDownloadAccess? Access)> AuthorizeDownloadWithTicketAsync(
        long userId,
        string attachmentId,
        string ticket,
        CancellationToken cancellationToken = default);

    string? TryResolveLocalPhysicalPath(string objectKey);

    Task<AttachmentReadResult?> OpenLocalContentAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    Task<AttachmentSignedUrl?> CreateSignedDownloadAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    Task<AttachmentDownloadDecision> AbandonAsync(
        long userId,
        string attachmentId,
        CancellationToken cancellationToken = default);
}
