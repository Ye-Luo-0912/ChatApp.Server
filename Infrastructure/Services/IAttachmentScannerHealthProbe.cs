namespace Infrastructure.Services;

/// <summary>Connectivity probe for an externally configured malware scanner.</summary>
public interface IAttachmentScannerHealthProbe
{
    Task ProbeAsync(CancellationToken cancellationToken = default);
}
