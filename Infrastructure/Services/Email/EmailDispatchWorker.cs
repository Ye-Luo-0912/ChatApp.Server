using Core.Models.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Email;

/// <summary>
/// 轮询 Outbox 并以有界并发发送邮件。
/// </summary>
public sealed class EmailDispatchWorker(
    IServiceScopeFactory scopeFactory,
    SmtpEmailSender smtp,
    EmailOutboxMetrics metrics,
    ILogger<EmailDispatchWorker> logger) : BackgroundService
{
    private const int MaxConcurrency = 4;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SentRetention = TimeSpan.FromDays(14);

    private readonly EmailOutboxDispatcher _dispatcher = new(
        scopeFactory,
        smtp.SendEmailAsync,
        metrics,
        logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
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

                var claimed = await _dispatcher.ClaimDueItemsAsync(stoppingToken).ConfigureAwait(false);

                foreach (var item in claimed)
                {
                    await semaphore.WaitAsync(stoppingToken).ConfigureAwait(false);
                    inFlight.Add(ProcessItemAsync(item, semaphore, stoppingToken));
                }

                inFlight.RemoveAll(static task => task.IsCompleted);
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

    private async Task ProcessItemAsync(
        EmailOutboxItem item, SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        try
        {
            await _dispatcher.ProcessItemAsync(item, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
