using Core.Interfaces;
using Core.Models.Export;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Services;

/// <summary>
/// Scope-safe adapter for the shared leased-job executor. Each operation gets
/// its own DbContext scope, so heartbeat/finalization cannot concurrently use
/// the claimer's scoped EF context.
/// </summary>
public sealed class AttachmentBlobDeleteJobStore(
    IServiceScopeFactory scopeFactory) : ILeasedJobStore<AttachmentBlobDeleteJob>
{
    public async Task<IReadOnlyList<AttachmentBlobDeleteJob>> ClaimAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AttachmentBlobDeleteService>();
        return await service.ClaimDueJobsAsync(maxCount, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LeaseRenewalResult> RenewAsync(
        AttachmentBlobDeleteJob job,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AttachmentBlobDeleteService>();
        return await service.RenewLeaseAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CompleteAsync(
        AttachmentBlobDeleteJob job,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AttachmentBlobDeleteService>();
        return await service.CompleteClaimedJobAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RetryAsync(
        AttachmentBlobDeleteJob job,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AttachmentBlobDeleteService>();
        return await service.RetryClaimedJobAsync(job, error, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeadLetterAsync(
        AttachmentBlobDeleteJob job,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AttachmentBlobDeleteService>();
        return await service.DeadLetterClaimedJobAsync(job, error, cancellationToken).ConfigureAwait(false);
    }
}
