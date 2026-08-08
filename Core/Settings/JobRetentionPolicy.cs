namespace Core.Settings;

/// <summary>
/// Central retention policy for durable worker history. Business workers do
/// not perform hidden cleanup in their hot polling paths.
/// </summary>
public sealed class JobRetentionPolicy
{
    public const string SectionName = "JobRetention";

    public int PollIntervalSeconds { get; set; } = 300;
    public int BatchSize { get; set; } = 500;
    public int ScanJobRetentionDays { get; set; } = 7;
    public int ScanProjectionRetentionDays { get; set; } = 7;
    public int ScanAuditRetentionDays { get; set; } = 90;
    public int AttachmentConfirmSagaRetentionDays { get; set; } = 7;
    public int AttachmentBlobDeleteRetentionDays { get; set; } = 30;
    public int LoginAuditRetentionDays { get; set; } = 90;
    public int LoginRiskRetentionDays { get; set; } = 30;
    public int AvatarFinalizationSagaRetentionDays { get; set; } = 7;
}
