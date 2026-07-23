using System.Security.Cryptography;
using Core.Interfaces;
using Core.Models.Attachment;
using Core.Models.Auth;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>正式附件上传编排：存储票 + realtime.attachments 元数据 + 鉴权下载。</summary>
public interface IAttachmentService
{
    Task<AttachmentPresignResponse> PresignAsync(
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

    Task<(AttachmentDownloadDecision Decision, AttachmentDownloadAccess? Access)> AuthorizeDownloadAsync(
        long userId,
        string attachmentId,
        CancellationToken cancellationToken = default);

    Task<AttachmentReadResult?> OpenLocalContentAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    Task<AttachmentSignedUrl?> CreateSignedDownloadAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    /// <summary>放弃未绑定附件并入队 blob 删除。NotFound/Forbidden 时返回对应决策。</summary>
    Task<AttachmentDownloadDecision> AbandonAsync(
        long userId,
        string attachmentId,
        CancellationToken cancellationToken = default);
}

public sealed class AttachmentService(
    IAttachmentStorage storage,
    IAttachmentMetadataStore metadata,
    IAttachmentBlobDeleteService blobDeletes,
    IAttachmentContentScanner contentScanner,
    IOptions<AttachmentStorageOptions> storageOptions,
    ILogger<AttachmentService> logger) : IAttachmentService
{
    public async Task<AttachmentPresignResponse> PresignAsync(
        long userId,
        AttachmentPresignRequest request,
        CancellationToken cancellationToken = default)
    {
        var (attachmentId, objectKey, ticket, uploadUrl, _, expiresAt) =
            await storage.CreateUploadTicketAsync(
                userId,
                request.ContentType,
                request.ContentLength,
                request.OriginalName,
                request.ClientAttachmentId,
                cancellationToken).ConfigureAwait(false);

        var downloadPath = AttachmentApiPaths.DownloadPath(attachmentId);

        if (metadata.IsAvailable)
        {
            try
            {
                await metadata.InsertTicketedAsync(
                    attachmentId,
                    userId,
                    objectKey,
                    publicUrl: null,
                    // octet-stream 仅作临时占位；Confirm 时魔数嗅探覆盖
                    request.ContentType,
                    request.ContentLength,
                    request.OriginalName,
                    request.ClientAttachmentId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 元数据失败不阻断上传票；confirm 会 upsert。
                logger.LogWarning(ex, "InsertTicketed 失败 AttachmentId={Id}", attachmentId);
            }
        }

#pragma warning disable CS0618 // PublicUrl deprecated but kept empty for wire compat
        return new AttachmentPresignResponse
        {
            AttachmentId = attachmentId,
            ObjectKey = objectKey,
            Ticket = ticket,
            UploadUrl = uploadUrl,
            DownloadPath = downloadPath,
            PublicUrl = string.Empty,
            ExpiresAt = expiresAt,
        };
#pragma warning restore CS0618
    }

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
            try
            {
                await metadata.MarkUploadedScanningAsync(
                        attachmentId, userId, sizeBytes, sha256Hex, cancellationToken)
                    .ConfigureAwait(false);
                AuthSecurityMetrics.AttachmentScan("uploaded_scanning");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MarkUploadedScanning 失败 AttachmentId={Id}", attachmentId);
            }
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

        // 内容扫描：魔数嗅探 → SHA-256 → 恶意软件钩子；失败 → Rejected
        var (scanOk, sniffedType, scanError) = await ScanContentAsync(
                objectKey, contentType, originalName, sizeBytes, cancellationToken)
            .ConfigureAwait(false);

        if (!scanOk)
        {
            if (metadata.IsAvailable)
            {
                try
                {
                    await metadata.MarkRejectedAsync(attachmentId, userId, scanError, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "MarkRejected 失败 AttachmentId={Id}", attachmentId);
                }
            }

            AuthSecurityMetrics.AttachmentScan("rejected");
            return (AuthOperationResult.Fail("ScanRejected", scanError ?? "附件内容扫描未通过"), null);
        }

        var finalContentType = sniffedType
                               ?? (string.IsNullOrWhiteSpace(contentType)
                                   ? "application/octet-stream"
                                   : contentType);

        if (metadata.IsAvailable)
        {
            try
            {
                await metadata.ConfirmAsync(
                    attachmentId,
                    userId,
                    objectKey,
                    publicUrl: null,
                    contentType: finalContentType,
                    sizeBytes: sizeBytes,
                    originalName: originalName,
                    cancellationToken).ConfigureAwait(false);
                AuthSecurityMetrics.AttachmentScan("confirmed");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Confirm 元数据写入失败 AttachmentId={Id}", attachmentId);
                return (AuthOperationResult.Fail("MetadataFailed", "附件元数据确认失败"), null);
            }
        }

#pragma warning disable CS0618
        return (AuthOperationResult.Success(), new ConfirmAttachmentResponse
        {
            AttachmentId = attachmentId,
            DownloadPath = downloadPath,
            ObjectKey = objectKey,
            PublicUrl = string.Empty,
        });
#pragma warning restore CS0618
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

    private async Task<(bool Ok, string? ContentType, string? Error)> ScanContentAsync(
        string objectKey,
        string? claimedContentType,
        string? originalName,
        long claimedSize,
        CancellationToken cancellationToken)
    {
        var read = await storage.OpenReadAsync(objectKey, cancellationToken).ConfigureAwait(false);
        if (read is null)
        {
            // S3：本地无法打开，仅做危险扩展名拒绝
            var s3Scan = await contentScanner.ScanAsync(
                    Stream.Null, claimedContentType, originalName, cancellationToken)
                .ConfigureAwait(false);
            if (!s3Scan.Allowed)
                return (false, null, s3Scan.Reason ?? "附件内容扫描未通过");

            var type = string.IsNullOrWhiteSpace(claimedContentType)
                ? "application/octet-stream"
                : claimedContentType;
            if (!storage.IsAllowedContentType(type))
                return (false, null, "不支持的附件格式");
            return (true, type, null);
        }

        string finalType;
        await using (read.Content)
        {
            var headerBuf = new byte[16];
            var headerLen = 0;
            while (headerLen < headerBuf.Length)
            {
                var n = await read.Content.ReadAsync(
                        headerBuf.AsMemory(headerLen, headerBuf.Length - headerLen), cancellationToken)
                    .ConfigureAwait(false);
                if (n == 0) break;
                headerLen += n;
            }

            finalType = AttachmentMagicSniffer.Sniff(headerBuf.AsSpan(0, headerLen))
                        ?? "application/octet-stream";
            if (!storage.IsAllowedContentType(finalType))
                return (false, null, "无法识别或不支持的附件内容类型");

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hasher.AppendData(headerBuf, 0, headerLen);
            var buffer = new byte[64 * 1024];
            long total = headerLen;
            var max = storageOptions.Value.MaxBytes;
            while (true)
            {
                var n = await read.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (n == 0) break;
                total += n;
                if (total > max)
                    return (false, null, "附件大小超限");
                hasher.AppendData(buffer, 0, n);
            }

            _ = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (claimedSize > 0 && Math.Abs(total - claimedSize) > Math.Max(1024, claimedSize / 10))
                return (false, null, "附件大小与元数据不一致");
        }

        var scanOpen = await storage.OpenReadAsync(objectKey, cancellationToken).ConfigureAwait(false);
        if (scanOpen is null)
            return (false, null, "附件内容不存在");

        await using (scanOpen.Content)
        {
            var scan = await contentScanner.ScanAsync(
                    scanOpen.Content, finalType, originalName, cancellationToken)
                .ConfigureAwait(false);
            if (!scan.Allowed)
                return (false, null, scan.Reason ?? "附件内容扫描未通过");
        }

        return (true, finalType, null);
    }
}
