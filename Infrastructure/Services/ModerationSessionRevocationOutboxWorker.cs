using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>在 Worker 角色中消费审核封禁会话撤销 Outbox。</summary>
public sealed class ModerationSessionRevocationOutboxWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ModerationSessionRevocationOutboxDispatcher> dispatcherLogger,
    ILogger<ModerationSessionRevocationOutboxWorker> logger) : BackgroundService
{
    private const int BatchSize = 16;
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dispatcher = new ModerationSessionRevocationOutboxDispatcher(
            scopeFactory,
            dispatcherLogger,
            batchSize: BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await dispatcher.ReclaimExpiredLeasesAsync(stoppingToken).ConfigureAwait(false);
                var items = await dispatcher.ClaimDueItemsAsync(BatchSize, stoppingToken).ConfigureAwait(false);
                if (items.Count == 0)
                {
                    await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var item in items)
                    await dispatcher.ProcessItemAsync(item, stoppingToken).ConfigureAwait(false);
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
