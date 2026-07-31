namespace Core.Interfaces;

/// <summary>
/// Delivers durable, fenced scan verdicts to the external attachment metadata
/// store. Delivery is at-least-once; Realtime state transitions must be idempotent.
/// </summary>
public interface IAttachmentScanProjectionService
{
    Task<int> ProcessDueAsync(CancellationToken cancellationToken = default);
}
