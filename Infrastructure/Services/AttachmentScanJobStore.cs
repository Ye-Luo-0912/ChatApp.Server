using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Adapts the content-scan durable queue to the common leased-job executor.
/// The scan service keeps the important two-phase meaning: a successful scan
/// stages a local projection and releases the scan lease; the separate
/// projection worker is responsible for the external side effects.
/// </summary>
public sealed class AttachmentScanJobStore(
    IServiceScopeFactory scopeFactory,
    IOptions<AttachmentStorageOptions> options) : ILeasedJobStore<AttachmentScanJob>
{
    public TimeSpan ProcessingLease => TimeSpan.FromMinutes(AttachmentScanService.LeaseMinutes);

    public int MaxAttempts => Math.Max(1, options.Value.MaxScanAttempts);

    public async Task<IReadOnlyList<AttachmentScanJob>> ClaimAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAttachmentScanService>();
        return await service.ClaimDueJobsAsync(maxCount, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LeaseRenewalResult> RenewAsync(
        AttachmentScanJob job,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAttachmentScanService>();
        return await service.RenewLeaseAsync(
                job.Id,
                job.LeaseOwner!,
                job.LeaseToken!,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> CompleteAsync(
        AttachmentScanJob job,
        CancellationToken cancellationToken = default)
    {
        // Normal scan execution returns AlreadyFinalized after atomically
        // changing Processing -> Finalizing and creating the projection.
        // Keep this method safe for callers using the default executor outcome.
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Data.UserDbContext>();
        var status = await db.AttachmentScanJobs
            .AsNoTracking()
            .Where(x => x.Id == job.Id)
            .Select(x => x.Status)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return status is AttachmentScanJobStatus.Finalizing or AttachmentScanJobStatus.Done;
    }

    public async Task<bool> RetryAsync(
        AttachmentScanJob job,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AttachmentScanService>();
        return await service.RetryClaimedJobAsync(job, error, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeadLetterAsync(
        AttachmentScanJob job,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AttachmentScanService>();
        return await service.DeadLetterClaimedJobAsync(job, error, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LeasedJobExecutionOutcome> ExecuteClaimedAsync(
        AttachmentScanJob job,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAttachmentScanService>();
        var result = await service.ProcessClaimedJobAsync(job, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            AttachmentScanProcessResult.ResultStaged => LeasedJobExecutionOutcome.AlreadyFinalized,
            AttachmentScanProcessResult.RetryScheduled => LeasedJobExecutionOutcome.RetryScheduled,
            _ => LeasedJobExecutionOutcome.LeaseLost,
        };
    }
}
