using Core.Interfaces;
using Core.Models.Security;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Core.Settings;

namespace Infrastructure.Services;

public sealed class LoginAuditOutboxWorker(
    LoginAuditOutboxDispatcher dispatcher,
    ILeasedJobStore<LoginAuditOutboxItem> store,
    LeasedJobExecutor<LoginAuditOutboxItem> executor,
    IOptions<WorkerConcurrencyOptions> workerOptions,
    ILogger<LoginAuditOutboxWorker> logger) : BackgroundService
{
    private const string WorkerName = "login_audit_outbox";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var completed = await executor.DrainAsync(
                        WorkerName,
                        Math.Max(1, workerOptions.Value.SecurityAudit),
                        LoginAuditOutboxDispatcher.DefaultLeaseDuration,
                        store,
                        dispatcher.ExecuteClaimedAsync,
                        item => item.AttemptCount + 1 >= dispatcher.MaxAttempts,
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
                logger.LogError(ex, "登录审计 Outbox 轮询异常");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
