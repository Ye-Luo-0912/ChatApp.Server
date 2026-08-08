namespace Core.Settings;

/// <summary>Role-aware readiness requirements. Explicit null values use safe feature defaults.</summary>
public sealed class HealthDependencyOptions
{
    public const string SectionName = "HealthDependencies";

    /// <summary>Set by the host composition root; not user-configurable.</summary>
    public string ProcessRole { get; set; } = "Api";
    public bool? AttachmentMetadataRequired { get; set; }
    public bool? MessageEvidenceRequired { get; set; }
    public bool? RealtimeOutboxRequired { get; set; }

    public bool IsWorker => string.Equals(ProcessRole, "Worker", StringComparison.OrdinalIgnoreCase);

    public bool RequireAttachmentMetadata(bool configured)
        => AttachmentMetadataRequired ?? configured;

    public bool RequireMessageEvidence(bool configured)
        => MessageEvidenceRequired ?? configured;

    public bool RequireRealtimeOutbox
        => RealtimeOutboxRequired ?? IsWorker;
}
