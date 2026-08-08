using Core.Interfaces;
using Core.Models.Export;
using Core.Models.Notifications;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Scope-safe adapter for notification outbox jobs. Every claim, heartbeat and
/// fenced terminal update uses a fresh DbContext; the executor can therefore
/// run jobs concurrently without sharing EF state.
/// </summary>
public sealed class NotificationOutboxJobStore(
    IServiceScopeFactory scopeFactory,
    NotificationOutboxMetrics metrics,
    ILogger<NotificationOutboxDispatcher> dispatcherLogger)
    : ILeasedJobStore<NotificationOutboxItem>, IReclaimCountSource
{
    private int _reclaimed;
    public TimeSpan ProcessingLease => TimeSpan.FromMinutes(2);

    public int MaxAttempts => 8;

    public async Task<IReadOnlyList<NotificationOutboxItem>> ClaimAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var dispatcher = new NotificationOutboxDispatcher(db, email, metrics, dispatcherLogger);
        var reclaimed = await dispatcher.ReclaimExpiredLeasesAsync(cancellationToken).ConfigureAwait(false);
        if (reclaimed > 0)
            Interlocked.Add(ref _reclaimed, reclaimed);
        return await dispatcher.ClaimDueItemsAsync(maxCount, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LeaseRenewalResult> RenewAsync(
        NotificationOutboxItem job,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dispatcher = CreateDispatcher(scope);
        return await dispatcher.RenewAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CompleteAsync(
        NotificationOutboxItem job,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dispatcher = CreateDispatcher(scope);
        return await dispatcher.CompleteClaimedAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RetryAsync(
        NotificationOutboxItem job,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dispatcher = CreateDispatcher(scope);
        return await dispatcher.RetryClaimedAsync(job, error, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeadLetterAsync(
        NotificationOutboxItem job,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dispatcher = CreateDispatcher(scope);
        return await dispatcher.DeadLetterClaimedAsync(job, error, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExecuteClaimedAsync(
        NotificationOutboxItem job,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dispatcher = CreateDispatcher(scope);
        await dispatcher.ExecuteClaimedAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> CountBacklogAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTimeOffset.UtcNow;
        return await db.NotificationOutbox.AsNoTracking()
            .CountAsync(
                x => (x.Status == NotificationOutboxStatus.Pending
                      || x.Status == NotificationOutboxStatus.Failed)
                     && x.NextAttemptAt <= now,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> GetOldestPendingJobCreatedAtAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTimeOffset.UtcNow;
        return await db.NotificationOutbox.AsNoTracking()
            .Where(x => (x.Status == NotificationOutboxStatus.Pending
                         || x.Status == NotificationOutboxStatus.Failed)
                        && x.NextAttemptAt <= now)
            .MinAsync(x => (DateTimeOffset?)x.CreatedAt, cancellationToken)
            .ConfigureAwait(false);
    }

    private NotificationOutboxDispatcher CreateDispatcher(AsyncServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        return new NotificationOutboxDispatcher(db, email, metrics, dispatcherLogger);
    }

    public int ConsumeReclaimedCount()
        => Interlocked.Exchange(ref _reclaimed, 0);
}
