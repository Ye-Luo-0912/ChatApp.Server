namespace Core.Interfaces;

/// <summary>
/// Durable blob boundary for generated user exports. Local storage is suitable
/// for development; production implementations must be shared across instances.
/// </summary>
public interface IDataExportBlobStore
{
    Task WriteAsync(
        string objectKey,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string objectKey,
        CancellationToken cancellationToken = default);
}
