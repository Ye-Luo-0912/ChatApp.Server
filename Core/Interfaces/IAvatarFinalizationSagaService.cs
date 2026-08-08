using Core.Models.Auth;
using Core.Models.Export;
using Core.Models.User;

namespace Core.Interfaces;

/// <summary>Durable avatar candidate finalization boundary.</summary>
public interface IAvatarFinalizationSagaService
{
    Task<(AuthOperationResult Result, AvatarFinalizationStatusDto? Response)> RequestAsync(
        long userId,
        string objectKey,
        string? ticket = null,
        CancellationToken cancellationToken = default);

    Task<AvatarFinalizationStatusDto?> GetStatusAsync(
        long userId,
        long sagaId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AvatarFinalizationSaga>> ClaimDueAsync(
        int batchSize,
        CancellationToken cancellationToken = default);

    Task ExecuteClaimedAsync(
        AvatarFinalizationSaga claimed,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteClaimedAsync(
        AvatarFinalizationSaga claimed,
        CancellationToken cancellationToken = default);

    Task<bool> RetryClaimedAsync(
        AvatarFinalizationSaga claimed,
        string error,
        CancellationToken cancellationToken = default);

    Task<bool> DeadLetterClaimedAsync(
        AvatarFinalizationSaga claimed,
        string error,
        CancellationToken cancellationToken = default);

    Task<LeaseRenewalResult> RenewLeaseAsync(
        AvatarFinalizationSaga claimed,
        CancellationToken cancellationToken = default);
}
