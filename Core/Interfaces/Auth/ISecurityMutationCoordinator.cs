using Core.Models.Security;

namespace Core.Interfaces.Auth;

/// <summary>
/// Single transaction boundary for durable account-security mutations.
/// The mutation callback must only change the current DbContext; the
/// coordinator persists the security event and advances the authorization
/// fence before commit.
/// </summary>
public interface ISecurityMutationCoordinator
{
    Task<SecurityMutationResult> ExecuteAsync(
        long userId,
        SecurityEventType eventType,
        string? detail,
        Func<CancellationToken, Task> mutateAsync,
        CancellationToken cancellationToken = default,
        Action<SecurityEvent>? configureEvent = null,
        SecurityMutationOptions? options = null);
}

public sealed record SecurityMutationResult(
    bool Succeeded,
    long? SecurityVersion,
    string? Error = null);

public sealed record SecurityMutationOptions(
    string? ExceptDeviceId = null,
    bool RevokeTrustedDevices = false,
    bool EnqueueSessionRevocation = true);
