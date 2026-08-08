using Core.Models.Security;
using Core.Settings;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>Worker-role consumer for the durable login-risk queue.</summary>
public sealed class LoginRiskAnalysisWorker(
    LoginRiskOutboxJobStore store,
    LeasedJobExecutor<LoginRiskOutboxItem> executor,
    IOptions<WorkerConcurrencyOptions> workerOptions,
    ILogger<LoginRiskAnalysisWorker> logger) : BackgroundService
{
    private const string WorkerName = "login_risk_analysis";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var completed = await executor.DrainAsync(
                        WorkerName,
                        Math.Max(1, workerOptions.Value.LoginRiskAnalysis),
                        LoginRiskOutboxJobStore.LeaseDuration,
                        store,
                        store.ExecuteClaimedAsync,
                        item => item.AttemptCount + 1 >= LoginRiskOutboxJobStore.MaxAttempts,
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
                logger.LogError(ex, "登录风险分析 Worker 轮询异常");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
