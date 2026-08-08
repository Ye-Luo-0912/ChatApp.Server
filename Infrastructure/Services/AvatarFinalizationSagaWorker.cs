using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public sealed class AvatarFinalizationSagaWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AvatarStorageOptions> options,
    IOptions<WorkerConcurrencyOptions> workerConcurrencyOptions,
    ILeasedJobStore<AvatarFinalizationSaga> sagaStore,
    LeasedJobExecutor<AvatarFinalizationSaga> executor,
    ILogger<AvatarFinalizationSagaWorker> logger) : BackgroundService
{
    private const string WorkerName = "avatar_finalization";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
        var poll = TimeSpan.FromSeconds(Math.Clamp(options.Value.FinalizationBackoffSeconds, 2, 60));
        var concurrency = Math.Max(1, workerConcurrencyOptions.Value.AvatarFinalization);
        var lease = TimeSpan.FromSeconds(Math.Clamp(options.Value.FinalizationLeaseSeconds, 30, 900));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var completed = await executor.DrainAsync(
                        WorkerName,
                        concurrency,
                        lease,
                        sagaStore,
                        ExecuteClaimedAsync,
                        saga => saga.AttemptCount + 1 >= Math.Max(1, options.Value.MaxFinalizationAttempts),
                        stoppingToken)
                    .ConfigureAwait(false);
                if (completed == 0)
                    await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "头像 Finalization Saga Worker 轮询异常");
                await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteClaimedAsync(
        AvatarFinalizationSaga claimed,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<IAvatarFinalizationSagaService>()
            .ExecuteClaimedAsync(claimed, cancellationToken)
            .ConfigureAwait(false);
    }
}
