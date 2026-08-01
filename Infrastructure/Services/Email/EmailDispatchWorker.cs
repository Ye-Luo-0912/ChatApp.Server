using Core.Models.Email;
using Core.Settings;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services.Email;

/// <summary>
/// 轮询 Outbox 并以有界并发发送邮件。
/// </summary>
public sealed class EmailDispatchWorker(
    IServiceScopeFactory scopeFactory,
    SmtpEmailSender smtp,
    IOptions<WorkerConcurrencyOptions> workerConcurrencyOptions,
    WorkerConcurrencyManager concurrencyManager,
    EmailOutboxMetrics metrics,
    ILogger<EmailDispatchWorker> logger) : BackgroundService
{
    private const string WorkerName = "email";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SentRetention = TimeSpan.FromDays(14);

    private readonly EmailOutboxDispatcher _dispatcher = new(
        scopeFactory,
        smtp.SendEmailAsync,
        metrics,
        logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerConcurrency = Math.Max(1, workerConcurrencyOptions.Value.EmailDispatch);
        var inFlight = new List<Task>();
        var lastCleanup = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _dispatcher.ReclaimStaleProcessingAsync(stoppingToken).ConfigureAwait(false);

                if (DateTime.UtcNow - lastCleanup > TimeSpan.FromHours(1))
                {
                    await _dispatcher.ArchiveSentAsync(SentRetention, stoppingToken).ConfigureAwait(false);
                    lastCleanup = DateTime.UtcNow;
                }

                inFlight.RemoveAll(static task => task.IsCompleted);
                var available = Math.Max(0, workerConcurrency - inFlight.Count);
                var reservations = new List<IAsyncDisposable>(available);
                while (reservations.Count < available
                       && concurrencyManager.TryAcquire(WorkerName, workerConcurrency, out var reservation))
                {
                    reservations.Add(reservation!);
                }

                if (reservations.Count == 0)
                {
                    await WaitForCapacityOrPollAsync(inFlight, PollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                IReadOnlyList<EmailOutboxItem> claimed;
                try
                {
                    claimed = await _dispatcher
                        .ClaimDueItemsAsync(reservations.Count, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    foreach (var reservation in reservations)
                        await reservation.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                for (var i = 0; i < claimed.Count; i++)
                {
                    inFlight.Add(ProcessItemAsync(claimed[i], reservations[i], stoppingToken));
                }
                for (var i = claimed.Count; i < reservations.Count; i++)
                    await reservations[i].DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "邮件 Outbox 轮询异常");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        await Task.WhenAll(inFlight).ConfigureAwait(false);
    }

    private static async Task WaitForCapacityOrPollAsync(
        IReadOnlyCollection<Task> inFlight, TimeSpan pollInterval, CancellationToken stoppingToken)
    {
        var nextPoll = Task.Delay(pollInterval, stoppingToken);
        if (inFlight.Count == 0)
        {
            await nextPoll.ConfigureAwait(false);
            return;
        }

        await Task.WhenAny(Task.WhenAny(inFlight), nextPoll).ConfigureAwait(false);
    }

    private async Task ProcessItemAsync(
        EmailOutboxItem item, IAsyncDisposable concurrencyScope, CancellationToken cancellationToken)
    {
        try
        {
            await _dispatcher.ProcessItemAsync(item, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await concurrencyScope.DisposeAsync().ConfigureAwait(false);
        }
    }
}
