using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Delivers scan-result projections independently of the long-running content
/// scanner. Reservation, claim, heartbeat, cancellation and fenced terminal
/// writes are shared with other leased workers.
/// </summary>
public sealed class AttachmentScanProjectionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AttachmentStorageOptions> options,
    IOptions<WorkerConcurrencyOptions> workerConcurrencyOptions,
    ILeasedJobStore<AttachmentScanProjection> projectionStore,
    LeasedJobExecutor<AttachmentScanProjection> executor,
    ILogger<AttachmentScanProjectionWorker> logger) : BackgroundService
{
    private const string WorkerName = "attachment_projection";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
        var poll = TimeSpan.FromSeconds(Math.Clamp(options.Value.ScanBackoffSeconds, 5, 60));
        var workerConcurrency = Math.Max(1, workerConcurrencyOptions.Value.AttachmentProjection);
        var leaseDuration = TimeSpan.FromSeconds(
            Math.Clamp(options.Value.ProjectionLeaseSeconds, 30, 900));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var completed = await executor.DrainAsync(
                        WorkerName,
                        workerConcurrency,
                        leaseDuration,
                        projectionStore,
                        ExecuteClaimedAsync,
                        job => job.AttemptCount + 1 >= Math.Max(1, options.Value.MaxScanAttempts),
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
                logger.LogWarning(ex, "附件扫描结果投递 Worker 异常");
                await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteClaimedAsync(
        AttachmentScanProjection claimed,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var projector = scope.ServiceProvider
            .GetRequiredService<AttachmentScanProjectionService>();
        await projector.ExecuteClaimedAsync(claimed, cancellationToken).ConfigureAwait(false);
    }
}
