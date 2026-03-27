namespace Core.Interfaces;

public interface IGeoLocationService
{
    Task<string?> GetLocationAsync(string? clientIp, CancellationToken cancellationToken = default);
}