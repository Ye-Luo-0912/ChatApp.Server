using Core.Interfaces;
using Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Delivers scan-result projections independently of the long-running content
/// scanner. A Realtime outage therefore retries a small durable outbox rather
/// than re-scanning the object or allowing an expired scanner lease to write it.
/// </summary>
public sealed class AttachmentScanProjectionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AttachmentStorageOptions> options,
    ILogger<AttachmentScanProjectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
        var poll = TimeSpan.FromSeconds(Math.Clamp(options.Value.ScanBackoffSeconds, 5, 60));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var projector = scope.ServiceProvider
                    .GetRequiredService<IAttachmentScanProjectionService>();
                var completed = await projector.ProcessDueAsync(stoppingToken).ConfigureAwait(false);
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
}
