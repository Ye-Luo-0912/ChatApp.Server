using Core.Models.Export;

namespace Core.Interfaces;

public interface IRealtimeOutboxAdminService
{
    Task<RealtimeOutboxSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default);

    Task<RealtimeOutboxListResponse> ListAsync(
        string? status = null,
        long? targetUserId = null,
        short? eventType = null,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<RealtimeOutboxItemDto?> GetAsync(
        string eventId,
        CancellationToken cancellationToken = default);

    Task<(bool Ok, string? Error)> ReplayDeadAsync(
        string eventId,
        CancellationToken cancellationToken = default);

    Task<RealtimeOutboxBatchReplayResult> ReplayDeadBatchAsync(
        IReadOnlyList<string> eventIds,
        CancellationToken cancellationToken = default);
}
