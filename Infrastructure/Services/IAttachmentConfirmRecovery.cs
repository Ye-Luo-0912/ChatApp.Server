namespace Infrastructure.Services;

/// <summary>
/// Recovers storage-confirmed state after a one-shot upload ticket was
/// consumed but the durable confirm Saga could not persist its progress.
/// Implementations must require evidence that ticketed confirmation already
/// reached storage; this is not a ticket-less public confirm operation.
/// </summary>
public interface IAttachmentConfirmRecovery
{
    Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? AttachmentId,
        string? ContentType, long SizeBytes, string? OriginalName, string? Error)>
        RecoverConfirmedObjectAsync(
            long userId,
            string objectKey,
            string attachmentId,
            CancellationToken cancellationToken = default);
}
