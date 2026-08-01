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
/// 附件内容扫描 Worker。
/// <para>P0-5.2：使用 <see cref="WorkerConcurrencyManager"/> 全局+专属并发预算；</para>
/// <para>只领取当前可并发处理的作业数量，每个作业在独立作用域中处理并配心跳续租；</para>
/// <para>扫描结论由 <see cref="IAttachmentScanService.ProcessClaimedJobAsync"/> 通过 LeaseToken fencing 持久化，外部投递另行执行。</para>
/// </summary>
public sealed class AttachmentScanWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AttachmentStorageOptions> options,
    IOptions<WorkerConcurrencyOptions> workerConcurrencyOptions,
    WorkerConcurrencyManager concurrencyManager,
    ILogger<AttachmentScanWorker> logger) : BackgroundService
{
    private const string WorkerName = "attachment_scan";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

        var workerConcurrency = Math.Max(1, workerConcurrencyOptions.Value.AttachmentScan);
        var poll = TimeSpan.FromSeconds(Math.Clamp(options.Value.ScanBackoffSeconds, 5, 60));
        var inFlight = new List<Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 只领取当前真正拥有执行槽的任务数量，避免一次领大批量后串行处理导致后续作业租约过期。
                // inFlight 已完成的清理后再计算可用槽位。
                inFlight.RemoveAll(static t => t.IsCompleted);
                var available = Math.Max(0, workerConcurrency - inFlight.Count);
                if (available == 0)
                {
                    await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var reservations = new List<IAsyncDisposable>(available);
                while (reservations.Count < available
                       && concurrencyManager.TryAcquire(WorkerName, workerConcurrency, out var reservation))
                {
                    reservations.Add(reservation!);
                }

                if (reservations.Count == 0)
                {
                    await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await using var scope = scopeFactory.CreateAsyncScope();
                var svc = scope.ServiceProvider.GetRequiredService<IAttachmentScanService>();
                IReadOnlyList<AttachmentScanJob> claimed;
                try
                {
                    claimed = await svc.ClaimDueJobsAsync(reservations.Count, stoppingToken).ConfigureAwait(false);
                }
                catch
                {
                    foreach (var reservation in reservations)
                        await reservation.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                if (claimed.Count == 0)
                {
                    foreach (var reservation in reservations)
                        await reservation.DisposeAsync().ConfigureAwait(false);
                    await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                for (var i = 0; i < claimed.Count; i++)
                {
                    inFlight.Add(ProcessOneAsync(claimed[i], reservations[i], stoppingToken));
                }
                for (var i = claimed.Count; i < reservations.Count; i++)
                    await reservations[i].DisposeAsync().ConfigureAwait(false);

                inFlight.RemoveAll(static t => t.IsCompleted);
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

        try
        {
            await Task.WhenAll(inFlight).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "附件扫描 Worker 关闭时等待在途任务失败");
        }
    }

    /// <summary>
    /// 单个作业处理：独立作用域 + 独立 DbContext，长扫描期间周期性续租。
    /// 扫描结论由 Service 以 LeaseToken 匹配持久化；仅真正租约丢失才计入 lease-lost 指标。
    /// </summary>
    private async Task ProcessOneAsync(
        AttachmentScanJob claimed, IAsyncDisposable concurrencyScope, CancellationToken cancellationToken)
    {
        // Keep the worker's cancellation local to this claimed job. A confirmed
        // lease loss must abort long object reads/AV scans rather than waiting for
        // the fenced terminal write to reject the stale worker.
        using var workCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseLossRecorded = 0;

        void CancelForLeaseLoss()
        {
            if (Interlocked.Exchange(ref leaseLossRecorded, 1) != 0)
                return;

            concurrencyManager.RecordLeaseLost(WorkerName);
            workCts.Cancel();
        }

        var heartbeatInterval = TimeSpan.FromMinutes(Math.Max(1, AttachmentScanService.LeaseMinutes / 3.0));
        var heartbeat = Task.Run(async () =>
        {
            while (!heartbeatCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(heartbeatInterval, heartbeatCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (heartbeatCts.Token.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var svc = scope.ServiceProvider.GetRequiredService<IAttachmentScanService>();
                    var renewed = await svc.RenewLeaseAsync(
                            claimed.Id, claimed.LeaseOwner!, claimed.LeaseToken!, heartbeatCts.Token)
                        .ConfigureAwait(false);
                    switch (renewed)
                    {
                        case LeaseRenewalResult.Renewed:
                            break;
                        case LeaseRenewalResult.LeaseLost:
                            CancelForLeaseLoss();
                            heartbeatCts.Cancel();
                            return;
                        case LeaseRenewalResult.TransientFailure:
                            logger.LogDebug(
                                "附件扫描租约续租暂时不可用 JobId={Id}，保留当前作业直到下次心跳",
                                claimed.Id);
                            break;
                    }
                }
                catch (OperationCanceledException) when (heartbeatCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(
                        ex,
                        "附件扫描心跳续租失败 JobId={Id}（可能已被重新领取）",
                        claimed.Id);
                }
            }
        }, CancellationToken.None);

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<IAttachmentScanService>();
            var result = await svc.ProcessClaimedJobAsync(claimed, workCts.Token).ConfigureAwait(false);
            if (result == AttachmentScanProcessResult.LeaseLost)
                CancelForLeaseLoss();
            if (result == AttachmentScanProcessResult.ResultStaged)
                logger.LogInformation("附件扫描结论已持久化 JobId={Id} AttachmentId={Aid}", claimed.Id, claimed.AttachmentId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 进程关闭：保持 Processing，租约过期后会被重新领取。
        }
        catch (OperationCanceledException) when (workCts.IsCancellationRequested)
        {
            logger.LogInformation(
                "附件扫描因租约已丢失而取消 JobId={Id} AttachmentId={AttachmentId}",
                claimed.Id,
                claimed.AttachmentId);
        }
        catch (Exception ex)
        {
            // Service 内部已对扫描异常做 fenced 重试；此处异常通常是基础设施问题，记录即可。
            logger.LogWarning(ex, "附件扫描处理异常 JobId={Id}", claimed.Id);
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "附件扫描心跳任务异常退出 JobId={Id}", claimed.Id);
            }
            await concurrencyScope.DisposeAsync().ConfigureAwait(false);
        }
    }
}
