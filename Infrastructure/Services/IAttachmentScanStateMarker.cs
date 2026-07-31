namespace Infrastructure.Services;

/// <summary>Optional object-store marker used by bucket lifecycle rules.</summary>
public interface IAttachmentScanStateMarker
{
    Task MarkScanStateAsync(
        string objectKey,
        string state,
        CancellationToken cancellationToken = default);
}
