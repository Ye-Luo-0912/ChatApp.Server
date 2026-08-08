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
    EmailOutboxJobStore jobStore,
    LeasedJobExecutor<EmailOutboxItem> executor,
    IOptions<WorkerConcurrencyOptions> workerConcurrencyOptions,
    ILogger<EmailDispatchWorker> logger) : BackgroundService
{
    private const string WorkerName = "email";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SentRetention = TimeSpan.FromDays(14);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerConcurrency = Math.Max(1, workerConcurrencyOptions.Value.EmailDispatch);
        var lastCleanup = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - lastCleanup > TimeSpan.FromHours(1))
                {
                    await jobStore.ArchiveSentAsync(SentRetention, stoppingToken)
                        .ConfigureAwait(false);
                    lastCleanup = DateTime.UtcNow;
                }

                var completed = await executor.DrainAsync(
                        WorkerName,
                        workerConcurrency,
                        jobStore.ProcessingLease,
                        jobStore,
                        jobStore.ExecuteClaimedAsync,
                        job => job.AttemptCount + 1 >= jobStore.MaxAttempts,
                        stoppingToken)
                    .ConfigureAwait(false);
                if (completed == 0)
                    await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
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

    }
}
