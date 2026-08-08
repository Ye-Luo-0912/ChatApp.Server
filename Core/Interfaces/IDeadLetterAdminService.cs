using Core.Models.Export;

namespace Core.Interfaces;

/// <summary>
/// One operational boundary over the heterogeneous durable queues. Queue
/// workers keep their own domain semantics; this interface only exposes safe,
/// fenced administrative state transitions.
/// </summary>
public interface IDeadLetterAdminService
{
    Task<DeadLetterPage> ListAsync(
        string? queue = null,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<DeadLetterItemDto?> GetAsync(
        string queue,
        string jobId,
        CancellationToken cancellationToken = default);

    Task<DeadLetterActionResult> ReplayAsync(
        long adminUserId,
        string queue,
        string jobId,
        string? reason,
        CancellationToken cancellationToken = default);

    Task<DeadLetterActionResult> SkipAsync(
        long adminUserId,
        string queue,
        string jobId,
        string? reason,
        CancellationToken cancellationToken = default);

    Task<DeadLetterActionResult> RepairAsync(
        long adminUserId,
        string queue,
        string jobId,
        string? reason,
        CancellationToken cancellationToken = default);
}
