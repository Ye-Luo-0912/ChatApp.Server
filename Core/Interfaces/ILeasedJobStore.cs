using Core.Models.Export;

namespace Core.Interfaces;

/// <summary>
/// Durable store boundary for a leased background job.
/// Implementations must fence every lease renewal and terminal update with the
/// job identity plus its current owner/token. A false terminal result means the
/// lease was no longer owned and the caller must discard the external result.
/// </summary>
public interface ILeasedJobStore<TJob>
{
    Task<IReadOnlyList<TJob>> ClaimAsync(
        int maxCount,
        CancellationToken cancellationToken = default);

    Task<LeaseRenewalResult> RenewAsync(
        TJob job,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteAsync(
        TJob job,
        CancellationToken cancellationToken = default);

    Task<bool> RetryAsync(
        TJob job,
        string error,
        CancellationToken cancellationToken = default);

    Task<bool> DeadLetterAsync(
        TJob job,
        string error,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional telemetry hook for stores that reclaim expired Processing rows as
/// part of ClaimAsync. It keeps the core store contract compatible with test
/// fakes and domain-specific queues that do not expose reclaim counts.
/// </summary>
public interface IReclaimCountSource
{
    int ConsumeReclaimedCount();
}
