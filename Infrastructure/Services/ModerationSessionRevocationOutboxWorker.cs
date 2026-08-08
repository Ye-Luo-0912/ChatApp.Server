using Core.Interfaces;
using Core.Models.Security;
using Core.Settings;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>在 Worker 角色中消费审核封禁会话撤销 Outbox。</summary>
public sealed class ModerationSessionRevocationOutboxWorker(
    ModerationSessionRevocationOutboxDispatcher dispatcher,
    ILeasedJobStore<ModerationSessionRevocationOutboxItem> store,
    LeasedJobExecutor<ModerationSessionRevocationOutboxItem> executor,
    IOptions<WorkerConcurrencyOptions> workerConcurrencyOptions,
    ILogger<ModerationSessionRevocationOutboxWorker> logger) : BackgroundService
{
    private const string WorkerName = "moderation_session_revocation";
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var completed = await executor.DrainAsync(
                        WorkerName,
                        Math.Max(1, workerConcurrencyOptions.Value.ModerationRevocation),
                        ModerationSessionRevocationOutboxDispatcher.DefaultLeaseDuration,
                        store,
                        dispatcher.ExecuteClaimedAsync,
                        item => item.AttemptCount + 1 >= dispatcher.MaxAttempts,
                        stoppingToken)
                    .ConfigureAwait(false);
                if (completed == 0)
                {
                    await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "审核会话撤销 Outbox 轮询异常");
                try
                {
                    await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
