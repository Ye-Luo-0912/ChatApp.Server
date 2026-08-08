namespace Core.Interfaces;

using Core.Models.Export;

/// <summary>
/// Delivers durable, fenced scan verdicts to the external attachment metadata
/// store. Delivery is at-least-once; Realtime state transitions must be idempotent.
/// </summary>
public interface IAttachmentScanProjectionService
{
    Task<int> ProcessDueAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AttachmentScanProjection>> ClaimDueAsync(
        int batchSize,
        CancellationToken cancellationToken = default);

    Task<AttachmentScanProjectionProcessResult> ProcessClaimedAsync(
        AttachmentScanProjection claimed,
        CancellationToken cancellationToken = default);

    Task<LeaseRenewalResult> RenewLeaseAsync(
        long projectionId,
        string leaseOwner,
        string leaseToken,
        CancellationToken cancellationToken = default);
}
