using Core.Interfaces;
using Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>处理附件内容扫描作业（瞬时失败退避重试）。</summary>
public sealed class AttachmentScanWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AttachmentStorageOptions> options,
    ILogger<AttachmentScanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var svc = scope.ServiceProvider.GetRequiredService<IAttachmentScanService>();
                var completed = await svc.ProcessDueAsync(stoppingToken).ConfigureAwait(false);
                if (completed > 0)
                    logger.LogInformation("附件扫描完成 {Count} 个作业", completed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "附件扫描 Worker 失败");
            }

            var delaySeconds = Math.Clamp(options.Value.ScanBackoffSeconds, 5, 60);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken).ConfigureAwait(false);
        }
    }
}
