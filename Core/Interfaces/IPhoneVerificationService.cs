using Core.Models;

namespace Core.Interfaces;

public interface IPhoneVerificationService
{
    Task<(bool Succeeded, string? Error)> SendCodeAsync(
        string e164PhoneNumber,
        CancellationToken cancellationToken = default);

    Task<(bool Succeeded, PhoneVerificationClaim? Claim, string? Error)> ClaimCodeAsync(
        string e164PhoneNumber,
        string code,
        CancellationToken cancellationToken = default);

    Task CompleteCodeAsync(PhoneVerificationClaim claim, CancellationToken cancellationToken = default);

    Task RestoreCodeAsync(PhoneVerificationClaim claim, CancellationToken cancellationToken = default);
}
