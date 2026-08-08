using Core.Interfaces.Auth;
using Core.Models.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Auth;

/// <summary>
/// Coordinates the durable portion of a security mutation. Redis session
/// cleanup, trusted-device cleanup and notifications remain post-commit
/// effects; the SecurityVersion fence and this event are committed first so a
/// failed derived-store action cannot leave old credentials authorized.
/// </summary>
public sealed class SecurityMutationCoordinator(
    UserDbContext db,
    ISecurityVersionAdvancer securityVersions,
    ILogger<SecurityMutationCoordinator> logger) : ISecurityMutationCoordinator
{
    public async Task<SecurityMutationResult> ExecuteAsync(
        long userId,
        SecurityEventType eventType,
        string? detail,
        Func<CancellationToken, Task> mutateAsync,
        CancellationToken cancellationToken = default,
        Action<SecurityEvent>? configureEvent = null,
        SecurityMutationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mutateAsync);
        if (userId <= 0)
            return new SecurityMutationResult(false, null, "invalid-user");

        var ownsTransaction = db.Database.IsRelational()
                              && db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            await mutateAsync(cancellationToken).ConfigureAwait(false);
            var securityEvent = new SecurityEvent
            {
                UserId = userId,
                EventType = eventType,
                Detail = detail,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            configureEvent?.Invoke(securityEvent);
            db.SecurityEvents.Add(securityEvent);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var version = await securityVersions
                .AdvanceSecurityVersionAsync(userId, cancellationToken)
                .ConfigureAwait(false);
            if (version is null)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new SecurityMutationResult(false, null, "security-version-advance-failed");
            }

            var mutationOptions = options ?? new SecurityMutationOptions();
            if (mutationOptions.EnqueueSessionRevocation)
            {
                db.SecuritySessionRevocationOutbox.Add(new SecuritySessionRevocationOutboxItem
                {
                    UserId = userId,
                    ExpectedSecurityVersion = version.Value,
                    ExceptDeviceId = string.IsNullOrWhiteSpace(mutationOptions.ExceptDeviceId)
                        ? null
                        : mutationOptions.ExceptDeviceId.Trim(),
                    RevokeTrustedDevices = mutationOptions.RevokeTrustedDevices,
                    EventType = eventType,
                    Status = SecuritySessionRevocationOutboxStatus.Pending,
                    NextAttemptAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SecurityMutationResult(true, version);
        }
        catch (OperationCanceledException)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            logger.LogWarning(ex, "安全变更回滚 UserId={UserId} Event={EventType}", userId, eventType);
            throw;
        }
    }
}
