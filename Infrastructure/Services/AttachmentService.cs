using Core.Interfaces;
using Core.Models.Attachment;
using Core.Models.Auth;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>正式附件上传编排：存储票 + realtime.attachments 元数据 + 鉴权下载。</summary>
public sealed class AttachmentService(
    IAttachmentStorage storage,
    IAttachmentMetadataStore metadata,
    IAttachmentBlobDeleteService blobDeletes,
    IAttachmentScanService scanJobs,
    IAttachmentDownloadTicketService downloadTickets,
    ILogger<AttachmentService> logger,
    IAttachmentConfirmSagaService? confirmSagas = null) : IAttachmentService
{
    public async Task<AttachmentPresignResult> PresignAsync(
        long userId,
        AttachmentPresignRequest request,
        CancellationToken cancellationToken = default)
    {
        // 秒传去重：客户端声明内容 SHA-256 时，先查本用户已确认/已绑定的内容。
        // 命中则签发去重票（无 PUT URL），客户端跳过上传直接确认；确认时服务端
        // 在存储层复制已扫描验证的对象，内容不再经客户端网络传输。
        if (IsContentAddress(request.Sha256) && metadata.IsAvailable)
        {
            var candidate = await metadata.TryFindDedupCandidateAsync(
                    userId, request.Sha256, cancellationToken)
                .ConfigureAwait(false);
            if (candidate is not null)
                return await PresignDeduplicatedAsync(
                        userId, request, candidate, cancellationToken)
                    .ConfigureAwait(false);
        }

        var (attachmentId, objectKey, ticket, uploadUrl, _, expiresAt) =
            await storage.CreateUploadTicketAsync(
                userId,
                request.ContentType,
                request.ContentLength,
                request.OriginalName,
                request.ClientAttachmentId,
                cancellationToken).ConfigureAwait(false);

        AttachmentUploadReservationStatus reservationStatus;
        try
        {
            reservationStatus = metadata.IsAvailable
                ? await metadata.ReserveTicketedAsync(
                    attachmentId,
                    userId,
                    objectKey,
                    publicUrl: null,
                    request.ContentType,
                    request.ContentLength,
                    request.OriginalName,
                    request.ClientAttachmentId,
                    cancellationToken).ConfigureAwait(false)
                : AttachmentUploadReservationStatus.MetadataUnavailable;
        }
        catch
        {
            await CancelUploadTicketSafeAsync(ticket).ConfigureAwait(false);
            throw;
        }

        AuthSecurityMetrics.AttachmentUploadReservation(ReservationOutcome(reservationStatus));
        if (reservationStatus != AttachmentUploadReservationStatus.Reserved)
        {
            await CancelUploadTicketSafeAsync(ticket).ConfigureAwait(false);
            return new AttachmentPresignResult(reservationStatus, null);
        }

        var downloadPath = AttachmentApiPaths.DownloadPath(attachmentId);

#pragma warning disable CS0618 // PublicUrl deprecated but kept empty for wire compat
        return new AttachmentPresignResult(
            reservationStatus,
            new AttachmentPresignResponse
            {
                AttachmentId = attachmentId,
                ObjectKey = objectKey,
                Ticket = ticket,
                UploadUrl = uploadUrl,
                DownloadPath = downloadPath,
                PublicUrl = string.Empty,
                ExpiresAt = expiresAt,
                UploadHeaders = storage is IAttachmentUploadHeadersProvider headers
                    ? headers.GetRequiredUploadHeaders(request.ContentType)
                    : null,
            });
#pragma warning restore CS0618
    }

    private async Task<AttachmentPresignResult> PresignDeduplicatedAsync(
        long userId,
        AttachmentPresignRequest request,
        AttachmentDedupCandidate candidate,
        CancellationToken cancellationToken)
    {
        var (attachmentId, objectKey, ticket, _, _, expiresAt) =
            await storage.CreateDedupTicketAsync(
                userId,
                candidate.ObjectKey,
                request.ContentType,
                request.ContentLength,
                request.Sha256!,
                request.OriginalName,
                request.ClientAttachmentId,
                cancellationToken).ConfigureAwait(false);

        AttachmentUploadReservationStatus reservationStatus;
        try
        {
            reservationStatus = metadata.IsAvailable
                ? await metadata.ReserveTicketedAsync(
                    attachmentId,
                    userId,
                    objectKey,
                    publicUrl: null,
                    request.ContentType,
                    request.ContentLength,
                    request.OriginalName,
                    request.ClientAttachmentId,
                    cancellationToken).ConfigureAwait(false)
                : AttachmentUploadReservationStatus.MetadataUnavailable;
        }
        catch
        {
            await CancelUploadTicketSafeAsync(ticket).ConfigureAwait(false);
            throw;
        }

        AuthSecurityMetrics.AttachmentUploadReservation(ReservationOutcome(reservationStatus));
        if (reservationStatus != AttachmentUploadReservationStatus.Reserved)
        {
            await CancelUploadTicketSafeAsync(ticket).ConfigureAwait(false);
            return new AttachmentPresignResult(reservationStatus, null);
        }

        var downloadPath = AttachmentApiPaths.DownloadPath(attachmentId);

#pragma warning disable CS0618 // PublicUrl deprecated but kept empty for wire compat
        return new AttachmentPresignResult(
            reservationStatus,
            new AttachmentPresignResponse
            {
                AttachmentId = attachmentId,
                ObjectKey = objectKey,
                Ticket = ticket,
                UploadUrl = string.Empty,
                DownloadPath = downloadPath,
                PublicUrl = string.Empty,
                ExpiresAt = expiresAt,
                Deduplicated = true,
            });
#pragma warning restore CS0618
    }

    private static bool IsContentAddress(string? sha256Hex) =>
        sha256Hex is not null
        && sha256Hex.Length == 64
        && sha256Hex.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private async Task CancelUploadTicketSafeAsync(string ticket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await storage.CancelUploadTicketAsync(ticket, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "附件预签失败后撤销上传票失败");
        }
    }

    private static string ReservationOutcome(AttachmentUploadReservationStatus status) => status switch
    {
        AttachmentUploadReservationStatus.Reserved => "reserved",
        AttachmentUploadReservationStatus.UnconfirmedObjectLimitExceeded => "pending_limit",
        AttachmentUploadReservationStatus.StorageBytesLimitExceeded => "storage_limit",
        AttachmentUploadReservationStatus.MetadataUnavailable => "metadata_unavailable",
        _ => "unexpected",
    };

    public async Task<AuthOperationResult> UploadAsync(
        long userId,
        string ticket,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var (ok, _, _, attachmentId, sizeBytes, sha256Hex, error) = await storage.StoreAsync(
            userId, ticket, content, contentType, cancellationToken).ConfigureAwait(false);
        if (!ok)
            return AuthOperationResult.Fail("UploadFailed", error ?? "附件上传失败");

        if (metadata.IsAvailable && !string.IsNullOrWhiteSpace(attachmentId))
        {
            await metadata.MarkUploadedScanningAsync(
                    attachmentId, userId, sizeBytes, sha256Hex, cancellationToken)
                .ConfigureAwait(false);
            AuthSecurityMetrics.AttachmentScan("uploaded_scanning");
        }

        return AuthOperationResult.Success();
    }

    public async Task<(AuthOperationResult Result, ConfirmAttachmentResponse? Body)> ConfirmAsync(
        long userId,
        ConfirmAttachmentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ObjectKey))
            return (AuthOperationResult.Fail("InvalidObjectKey", "无效的附件对象键"), null);

        // Production uses a durable local intent. The legacy branch remains for
        // direct compatibility tests that construct AttachmentService manually.
        if (confirmSagas is not null)
            return await confirmSagas.RequestAsync(userId, request, cancellationToken)
                .ConfigureAwait(false);

        var (ok, _, objectKey, attachmentId, contentType, sizeBytes, originalName, error) =
            await storage.ConfirmObjectAsync(
                userId,
                request.ObjectKey,
                request.Ticket,
                request.AttachmentId,
                cancellationToken).ConfigureAwait(false);

        if (!ok || string.IsNullOrWhiteSpace(objectKey) || string.IsNullOrWhiteSpace(attachmentId))
        {
            return (AuthOperationResult.Fail("ConfirmFailed", error ?? "附件确认失败"), null);
        }

        var downloadPath = AttachmentApiPaths.DownloadPath(attachmentId);

        // 保持 Scanning：内容扫描改由后台作业执行（瞬时失败可退避重试）
        if (metadata.IsAvailable)
        {
            await metadata.MarkUploadedScanningAsync(
                    attachmentId, userId, sizeBytes, sha256Hex: null, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await scanJobs.EnqueueAsync(
                    attachmentId,
                    userId,
                    objectKey,
                    contentType,
                    originalName,
                    sizeBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "附件扫描入队失败 AttachmentId={Id}", attachmentId);
            try
            {
                // The metadata row is already visible as Scanning. If the scan
                // job could not be durably recorded, delete the object through
                // the same tombstone path instead of leaving an unreachable blob.
                await blobDeletes.EnqueueAsync(
                        [(objectKey, attachmentId)], userId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception deleteEx)
            {
                logger.LogError(
                    deleteEx,
                    "扫描入队失败后附件删除任务入队失败 AttachmentId={Id} Key={Key}",
                    attachmentId,
                    objectKey);
            }
            return (AuthOperationResult.Fail("ScanEnqueueFailed", "附件扫描入队失败"), null);
        }

#pragma warning disable CS0618
        return (AuthOperationResult.Success(), new ConfirmAttachmentResponse
        {
            AttachmentId = attachmentId,
            DownloadPath = downloadPath,
            ObjectKey = objectKey,
            Status = "Scanning",
            PublicUrl = string.Empty,
        });
#pragma warning restore CS0618
    }

    public async Task<AttachmentLifecycleStatusDto?> GetStatusAsync(
        long userId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(attachmentId) || !metadata.IsAvailable)
            return null;

        var record = await metadata.GetStatusForUploaderAsync(
                attachmentId, userId, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
            return null;

        return new AttachmentLifecycleStatusDto(
            record.AttachmentId,
            record.Status,
            record.Status.ToString(),
            record.SizeBytes,
            record.ContentType,
            record.OriginalName,
            record.CreatedAtMs,
            record.ConfirmedAtMs,
            record.BoundAtMs,
            AttachmentApiPaths.DownloadPath(record.AttachmentId));
    }

    public async Task<(AttachmentDownloadDecision Decision, AttachmentDownloadAccess? Access)> AuthorizeDownloadAsync(
        long userId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(attachmentId))
            return (AttachmentDownloadDecision.NotFound, null);

        if (!metadata.IsAvailable)
            return (AttachmentDownloadDecision.Unavailable, null);

        var access = await metadata.ResolveDownloadAccessAsync(attachmentId, userId, cancellationToken)
            .ConfigureAwait(false);
        return (access.Decision, access);
    }

    public async Task<(AttachmentDownloadDecision Decision, AttachmentDownloadTicketResponse? Body)> IssueDownloadTicketAsync(
        long userId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        var (decision, _) = await AuthorizeDownloadAsync(userId, attachmentId, cancellationToken)
            .ConfigureAwait(false);
        if (decision != AttachmentDownloadDecision.Allowed)
            return (decision, null);

        var (ticket, expiresAt) = await downloadTickets.IssueAsync(userId, attachmentId, cancellationToken)
            .ConfigureAwait(false);

        return (AttachmentDownloadDecision.Allowed, new AttachmentDownloadTicketResponse
        {
            AttachmentId = attachmentId,
            Ticket = ticket,
            ExpiresAt = expiresAt,
            DownloadUrl = AttachmentApiPaths.DownloadPathWithTicket(attachmentId, ticket),
        });
    }

    public async Task<(AttachmentDownloadDecision Decision, AttachmentDownloadAccess? Access)> AuthorizeDownloadWithTicketAsync(
        long userId,
        string attachmentId,
        string ticket,
        CancellationToken cancellationToken = default)
    {
        var payload = await downloadTickets.PeekAsync(ticket, cancellationToken).ConfigureAwait(false);
        if (payload is null
            || payload.UserId != userId
            || !string.Equals(payload.AttachmentId, attachmentId, StringComparison.Ordinal))
        {
            return (AttachmentDownloadDecision.InvalidTicket, null);
        }

        var access = await AuthorizeDownloadAsync(userId, attachmentId, cancellationToken)
            .ConfigureAwait(false);
        if (access.Decision != AttachmentDownloadDecision.Allowed)
            return access;

        var consumed = await downloadTickets.TryConsumeAsync(ticket, cancellationToken)
            .ConfigureAwait(false);
        if (consumed is null
            || consumed.UserId != userId
            || !string.Equals(consumed.AttachmentId, attachmentId, StringComparison.Ordinal))
        {
            return (AttachmentDownloadDecision.InvalidTicket, null);
        }

        return access;
    }

    public string? TryResolveLocalPhysicalPath(string objectKey)
        => storage.TryResolveLocalPhysicalPath(objectKey);

    public Task<AttachmentReadResult?> OpenLocalContentAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
        => storage.OpenReadAsync(objectKey, cancellationToken);

    public Task<AttachmentSignedUrl?> CreateSignedDownloadAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
        => storage.CreateSignedDownloadUrlAsync(objectKey, ttl: null, cancellationToken);

    public async Task<AttachmentDownloadDecision> AbandonAsync(
        long userId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(attachmentId))
            return AttachmentDownloadDecision.NotFound;

        if (!metadata.IsAvailable)
            return AttachmentDownloadDecision.Unavailable;

        var objectKey = await metadata
            .TryAbandonUnboundByUploaderAsync(attachmentId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(objectKey))
            return AttachmentDownloadDecision.Forbidden;

        try
        {
            await blobDeletes
                .EnqueueAsync([objectKey], userId, attachmentId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "放弃附件后 blob 删除入队失败 AttachmentId={Id} ObjectKey={Key}",
                attachmentId,
                objectKey);
        }

        return AttachmentDownloadDecision.Allowed;
    }
}
