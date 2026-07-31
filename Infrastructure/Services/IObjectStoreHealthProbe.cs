namespace Infrastructure.Services;

/// <summary>Lightweight connectivity probe used by readiness/capability health checks.</summary>
public interface IObjectStoreHealthProbe
{
    Task ProbeAsync(CancellationToken cancellationToken = default);
}
