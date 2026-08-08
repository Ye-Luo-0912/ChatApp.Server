using Core.Interfaces;
using Core.Models.Export;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Core.Settings;

namespace Infrastructure.Services;

/// <summary>
/// Adapts the user-row deletion lease to the shared leased-job executor.
/// The account row itself is the durable queue and the purge transaction is
/// the terminal fenced write, so no second deletion status table is needed.
/// </summary>
public sealed class AccountDeletionJobStore(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerConcurrencyOptions>? workerOptions = null) : ILeasedJobStore<AccountDeletionJob>
{
    public int MaxAttempts => Math.Max(1, workerOptions?.Value.AccountDeletionMaxAttempts ?? 5);
    public async Task<IReadOnlyList<AccountDeletionJob>> ClaimAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountLifecycleService>();
        return await service.ClaimDueDeletionJobsAsync(maxCount, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LeaseRenewalResult> RenewAsync(
        AccountDeletionJob job,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountLifecycleService>();
        return await service.RenewDeletionLeaseAsync(job, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> CompleteAsync(
        AccountDeletionJob job,
        CancellationToken cancellationToken = default)
    {
        if (job.Terminal)
            return true;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var state = await db.Users.AsNoTracking()
            .Where(user => user.Id == job.UserId)
            .Select(user => new
            {
                user.DeletionScheduledAt,
                user.DeletionLeaseOwner,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // A concurrent cancel or an already committed purge is a legitimate
        // terminal outcome. A scheduled row owned by another token is a lost
        // lease and must not be acknowledged by this worker.
        return state is null
               || state.DeletionScheduledAt is null;
    }

    public async Task<bool> RetryAsync(
        AccountDeletionJob job,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountLifecycleService>();
        return await service.ReleaseDeletionLeaseAsync(job, error, deadLetter: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> DeadLetterAsync(
        AccountDeletionJob job,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountLifecycleService>();
        return await service.ReleaseDeletionLeaseAsync(job, error, deadLetter: true, cancellationToken)
            .ConfigureAwait(false);
    }
}
