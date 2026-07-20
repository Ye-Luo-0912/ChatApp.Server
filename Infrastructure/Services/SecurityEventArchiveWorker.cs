using Core.Interfaces;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>定期清理过期安全事件。</summary>
public sealed class SecurityEventArchiveWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<SecurityEventArchiveWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(180);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ArchiveAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "安全事件归档失败");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ArchiveAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var cutoff = DateTimeOffset.UtcNow - Retention;
        var deleted = await db.SecurityEvents
            .Where(e => e.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        if (deleted > 0)
            logger.LogInformation("已清理过期安全事件 {Count} 条（保留 {Days} 天）", deleted, Retention.TotalDays);
    }
}
