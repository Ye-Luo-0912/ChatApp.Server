using Core.Models.Export;

namespace Core.Interfaces;

/// <summary>
/// Optional target-side CAS contract for scan projection writes. Realtime
/// implementations use the projection id and scan generation to reject stale
/// retries; legacy metadata fakes may continue using the base interface.
/// </summary>
public interface IAttachmentScanProjectionMetadataStore
{
    Task<AttachmentProjectionWriteResult> MarkUploadedScanningAsync(
        string attachmentId,
        long uploaderUserId,
        long sizeBytes,
        long projectionId,
        long scanVersion,
        string? sha256Hex = null,
        CancellationToken cancellationToken = default);

    Task<AttachmentProjectionWriteResult> ConfirmAsync(
        string attachmentId,
        long uploaderUserId,
        string objectKey,
        string? publicUrl,
        string contentType,
        long sizeBytes,
        string? originalName,
        long projectionId,
        long scanVersion,
        CancellationToken cancellationToken = default);

    Task<AttachmentProjectionWriteResult> MarkRejectedAsync(
        string attachmentId,
        long uploaderUserId,
        string? reason,
        long projectionId,
        long scanVersion,
        CancellationToken cancellationToken = default);

    Task<AttachmentProjectionWriteResult> MarkAbandonedAsync(
        string attachmentId,
        long uploaderUserId,
        long projectionId,
        long scanVersion,
        CancellationToken cancellationToken = default);
}
