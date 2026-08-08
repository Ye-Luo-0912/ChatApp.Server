using Core.Models.Export;
using Core.Settings;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// 内容扫描 Worker。领取数量先由通用执行器按真实容量保留，随后由每个
/// 作业独立心跳、取消和 fenced 结果暂存；扫描投影由另一个 Worker 消费。
/// </summary>
public sealed class AttachmentScanWorker(
    AttachmentScanJobStore jobStore,
    LeasedJobExecutor<AttachmentScanJob> executor,
    IOptions<AttachmentStorageOptions> options,
    IOptions<WorkerConcurrencyOptions> workerConcurrencyOptions,
    ILogger<AttachmentScanWorker> logger) : BackgroundService
{
    private const string WorkerName = "attachment_scan";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
        var poll = TimeSpan.FromSeconds(Math.Clamp(options.Value.ScanBackoffSeconds, 5, 60));
        var bytesPerScan = Math.Max(1L, options.Value.MaxBytes);
        var byteConcurrency = Math.Max(
            1L,
            options.Value.ScanMaxConcurrentBytes / bytesPerScan);
        var workerConcurrency = Math.Max(
            1,
            Math.Min(
                workerConcurrencyOptions.Value.AttachmentScan,
                (int)Math.Min(int.MaxValue, byteConcurrency)));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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
                    await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "附件扫描 Worker 轮询异常");
                await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
