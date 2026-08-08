using Core.Interfaces;
using Core.Models.Export;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Services;

/// <summary>
/// Scope-safe adapter for the shared attachment-confirm leased-job executor.
/// Claim, heartbeat and fenced terminal operations each use a fresh scope;
/// the long-running external stages are executed by the worker scope supplied
/// to the executor.
/// </summary>
public sealed class AttachmentConfirmSagaJobStore(
    IServiceScopeFactory scopeFactory) : ILeasedJobStore<AttachmentConfirmSaga>
{
    public async Task<IReadOnlyList<AttachmentConfirmSaga>> ClaimAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAttachmentConfirmSagaService>();
        return await service.ClaimDueAsync(maxCount, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LeaseRenewalResult> RenewAsync(
        AttachmentConfirmSaga job,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAttachmentConfirmSagaService>();
        return await service.RenewLeaseAsync(
                job.Id,
                job.LeaseOwner!,
                job.LeaseToken!,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> CompleteAsync(
        AttachmentConfirmSaga job,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAttachmentConfirmSagaService>();
        return await service.CompleteClaimedAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RetryAsync(
        AttachmentConfirmSaga job,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAttachmentConfirmSagaService>();
        return await service.RetryClaimedAsync(job, error, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeadLetterAsync(
        AttachmentConfirmSaga job,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAttachmentConfirmSagaService>();
        return await service.DeadLetterClaimedAsync(job, error, cancellationToken).ConfigureAwait(false);
    }
}
