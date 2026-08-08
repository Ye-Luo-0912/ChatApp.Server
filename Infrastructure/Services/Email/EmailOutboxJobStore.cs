using Core.Interfaces;
using Core.Models.Email;
using Core.Models.Export;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Email;

/// <summary>
/// Scope-safe adapter for the shared leased-job executor. The dispatcher owns
/// one process-wide owner id so claim, heartbeat and terminal writes use the
/// same fencing identity across fresh DbContext scopes.
/// </summary>
public sealed class EmailOutboxJobStore : ILeasedJobStore<EmailOutboxItem>, IReclaimCountSource
{
    private readonly EmailOutboxDispatcher _dispatcher;
    private int _reclaimed;

    public EmailOutboxJobStore(
        IServiceScopeFactory scopeFactory,
        SmtpEmailSender smtp,
        EmailOutboxMetrics metrics,
        ILogger<EmailOutboxDispatcher> logger)
    {
        _dispatcher = new EmailOutboxDispatcher(
            scopeFactory,
            smtp.SendEmailAsync,
            metrics,
            logger);
    }

    public TimeSpan ProcessingLease => _dispatcher.ProcessingLease;

    public int MaxAttempts => _dispatcher.MaxAttempts;

    public Task<int> ArchiveSentAsync(
        TimeSpan retention,
        CancellationToken cancellationToken = default)
        => _dispatcher.ArchiveSentAsync(retention, cancellationToken);

    public async Task<IReadOnlyList<EmailOutboxItem>> ClaimAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        var reclaimed = await _dispatcher.ReclaimStaleProcessingAsync(cancellationToken)
            .ConfigureAwait(false);
        if (reclaimed > 0)
            Interlocked.Add(ref _reclaimed, reclaimed);
        return await _dispatcher.ClaimDueItemsAsync(maxCount, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<LeaseRenewalResult> RenewAsync(
        EmailOutboxItem job,
        CancellationToken cancellationToken = default)
        => _dispatcher.RenewAsync(job, cancellationToken);

    public Task ExecuteClaimedAsync(
        EmailOutboxItem job,
        CancellationToken cancellationToken = default)
        => _dispatcher.ExecuteClaimedAsync(job, cancellationToken);

    public Task<bool> CompleteAsync(
        EmailOutboxItem job,
        CancellationToken cancellationToken = default)
        => _dispatcher.CompleteClaimedAsync(job, cancellationToken);

    public Task<bool> RetryAsync(
        EmailOutboxItem job,
        string error,
        CancellationToken cancellationToken = default)
        => _dispatcher.RetryClaimedAsync(job, error, cancellationToken);

    public Task<bool> DeadLetterAsync(
        EmailOutboxItem job,
        string error,
        CancellationToken cancellationToken = default)
        => _dispatcher.DeadLetterClaimedAsync(job, error, cancellationToken);

    public int ConsumeReclaimedCount()
        => Interlocked.Exchange(ref _reclaimed, 0);
}
