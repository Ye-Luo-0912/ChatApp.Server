namespace Infrastructure.Services;

/// <summary>Optional object-store marker used by bucket lifecycle rules.</summary>
public interface IAttachmentScanStateMarker
{
    Task MarkScanStateAsync(
        string objectKey,
        string state,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Promotes the exact object version that passed scanning into a key which was
/// never exposed through a client-writable URL.
/// </summary>
public interface IAttachmentScanFinalizer
{
    Task<AttachmentFinalizedObject> FinalizeConfirmedAsync(
        string attachmentId,
        long userId,
        string sourceObjectKey,
        string? expectedEntityTag,
        CancellationToken cancellationToken = default);
}

public sealed record AttachmentFinalizedObject(
    string ObjectKey,
    string? StagingObjectKeyToDelete);
