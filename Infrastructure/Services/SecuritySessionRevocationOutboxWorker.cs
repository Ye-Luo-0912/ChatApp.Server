using Core.Interfaces;
using Core.Models.Security;
using Core.Settings;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public sealed class SecuritySessionRevocationOutboxWorker(
    SecuritySessionRevocationOutboxDispatcher dispatcher,
    ILeasedJobStore<SecuritySessionRevocationOutboxItem> store,
    LeasedJobExecutor<SecuritySessionRevocationOutboxItem> executor,
    IOptions<WorkerConcurrencyOptions> workerOptions,
    ILogger<SecuritySessionRevocationOutboxWorker> logger) : BackgroundService
{
    private const string WorkerName = "security_session_revocation";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var completed = await executor.DrainAsync(
                        WorkerName,
                        Math.Max(1, workerOptions.Value.SecurityRevocation),
                        SecuritySessionRevocationOutboxDispatcher.DefaultLeaseDuration,
                        store,
                        dispatcher.ExecuteClaimedAsync,
                        item => item.AttemptCount + 1 >= dispatcher.MaxAttempts,
                        stoppingToken)
                    .ConfigureAwait(false);
                if (completed == 0)
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "安全会话撤销 Outbox 轮询异常");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
