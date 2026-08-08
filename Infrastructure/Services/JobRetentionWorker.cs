using Core.Interfaces;
using Core.Settings;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public sealed class JobRetentionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<JobRetentionPolicy> options,
    ILogger<JobRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    var retention = scope.ServiceProvider.GetRequiredService<IJobRetentionService>();
                    await retention.PurgeAsync(stoppingToken).ConfigureAwait(false);
                }
                await Task.Delay(
                        TimeSpan.FromSeconds(Math.Clamp(options.Value.PollIntervalSeconds, 30, 86_400)),
                        stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                JobRetentionMetrics.RecordFailure("all");
                logger.LogError(ex, "统一 Worker retention 轮询异常");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
