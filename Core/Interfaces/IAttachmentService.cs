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

    Task<AuthOperationResult> UploadAsync(
        long userId,
        string ticket,
        Stream content,
        string contentType,
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
