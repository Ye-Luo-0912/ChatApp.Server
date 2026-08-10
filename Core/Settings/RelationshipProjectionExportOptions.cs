namespace Core.Settings;

public sealed class RelationshipProjectionExportOptions
{
    public const string SectionName = "RelationshipProjectionExport";
    public const string ApiKeyHeaderName = "X-Relationship-Projection-Key";

    /// <summary>Service-to-service secret. Keep it in environment/user secrets, never appsettings.</summary>
    public string? ApiKey { get; set; }
}
