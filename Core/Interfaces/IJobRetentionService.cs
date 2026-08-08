namespace Core.Interfaces;

public sealed record JobRetentionResult(
    int ScanJobs,
    int ScanProjections,
    int ScanAudits,
    int AttachmentBlobDeleteJobs,
    int LoginAuditOutbox,
    int LoginRiskOutbox,
    int AttachmentConfirmSagas = 0,
    int AvatarFinalizationSagas = 0)
{
    public int Total => ScanJobs
                        + ScanProjections
                        + ScanAudits
                        + AttachmentBlobDeleteJobs
                        + LoginAuditOutbox
                        + LoginRiskOutbox
                        + AttachmentConfirmSagas
                        + AvatarFinalizationSagas;
}

/// <summary>Single durable retention boundary for completed worker records.</summary>
public interface IJobRetentionService
{
    Task<JobRetentionResult> PurgeAsync(CancellationToken cancellationToken = default);
}
