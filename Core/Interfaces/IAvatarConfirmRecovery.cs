namespace Core.Interfaces;

/// <summary>
/// Re-proves a storage-confirmed avatar without consuming the upload ticket a
/// second time after a crash between object finalization and saga persistence.
/// </summary>
public interface IAvatarConfirmRecovery
{
    Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? Error)> RecoverConfirmedObjectAsync(
        long userId,
        string objectKey,
        CancellationToken cancellationToken = default);
}
