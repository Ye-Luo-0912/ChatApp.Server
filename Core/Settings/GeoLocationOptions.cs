namespace Core.Settings;

/// <summary>
/// GeoIP data-source policy. A local CIDR database is preferred; the external
/// provider is an explicit opt-in fallback because it receives client IPs.
/// </summary>
public sealed class GeoLocationOptions
{
    public const string SectionName = "GeoLocation";

    public string? LocalDatabasePath { get; set; }

    /// <summary>
    /// When true, use the configured HTTPS provider after a local miss.
    /// Keep false when no third-party IP transfer is approved.
    /// </summary>
    public bool AllowExternalFallback { get; set; }

    public int MaxLocalEntries { get; set; } = 250_000;
}
