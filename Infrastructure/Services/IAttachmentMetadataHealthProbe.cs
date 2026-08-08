namespace Infrastructure.Services;

/// <summary>Connectivity probe for the configured Realtime attachment store.</summary>
public interface IAttachmentMetadataHealthProbe
{
    Task ProbeAsync(CancellationToken cancellationToken = default);
}
