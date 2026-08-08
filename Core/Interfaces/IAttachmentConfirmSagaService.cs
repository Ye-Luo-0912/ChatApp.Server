using Core.Models.Attachment;
using Core.Models.Auth;
using Core.Models.Export;

namespace Core.Interfaces;

/// <summary>Durable, retryable attachment-confirm orchestration boundary.</summary>
public interface IAttachmentConfirmSagaService
{
    Task<(AuthOperationResult Result, ConfirmAttachmentResponse? Response)> RequestAsync(
        long userId,
        ConfirmAttachmentRequest request,
        CancellationToken cancellationToken = default);

    Task<ConfirmAttachmentResponse?> GetStatusAsync(
        long userId,
        string attachmentId,
        CancellationToken cancellationToken = default);

    Task<int> ProcessDueAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AttachmentConfirmSaga>> ClaimDueAsync(
        int batchSize,
        CancellationToken cancellationToken = default);

    Task<bool> ProcessClaimedAsync(
        AttachmentConfirmSaga claimed,
        CancellationToken cancellationToken = default);

    /// <summary>Executes external stages and fenced intermediate transitions.</summary>
    Task ExecuteClaimedAsync(
        AttachmentConfirmSaga claimed,
        CancellationToken cancellationToken = default);

    /// <summary>Fenced local terminal cleanup after external stages succeed.</summary>
    Task<bool> CompleteClaimedAsync(
        AttachmentConfirmSaga claimed,
        CancellationToken cancellationToken = default);

    Task<bool> RetryClaimedAsync(
        AttachmentConfirmSaga claimed,
        string error,
        CancellationToken cancellationToken = default);

    Task<bool> DeadLetterClaimedAsync(
        AttachmentConfirmSaga claimed,
        string error,
        CancellationToken cancellationToken = default);

    Task<LeaseRenewalResult> RenewLeaseAsync(
        long sagaId,
        string leaseOwner,
        string leaseToken,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a queued scan complete; safe to call more than once.</summary>
    Task CompleteScanAsync(
        string attachmentId,
        long userId,
        CancellationToken cancellationToken = default);
}
