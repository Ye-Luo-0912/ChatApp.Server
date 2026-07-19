using Core.Models.Email;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Email;

/// <summary>
/// 轮询 Outbox 并以有界并发发送邮件。
/// </summary>
public sealed class EmailDispatchWorker(
    IServiceScopeFactory scopeFactory,
    SmtpEmailSender smtp,
    EmailOutboxMetrics metrics,
    ILogger<EmailDispatchWorker> logger) : BackgroundService
{
    private const int MaxConcurrency = 4;
    private const int MaxAttempts = 5;
    private const int BatchSize = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        var inFlight = new List<Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimed = await ClaimDueItemsAsync(stoppingToken).ConfigureAwait(false);

                foreach (var item in claimed)
                {
                    await semaphore.WaitAsync(stoppingToken).ConfigureAwait(false);
                    inFlight.Add(ProcessItemAsync(item, semaphore, stoppingToken));
                }

                inFlight.RemoveAll(static task => task.IsCompleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "邮件 Outbox 轮询异常");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        await Task.WhenAll(inFlight).ConfigureAwait(false);
    }

    private async Task<List<EmailOutboxItem>> ClaimDueItemsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTime.UtcNow;

        var dueIds = await db.EmailOutbox
            .AsNoTracking()
            .Where(x =>
                (x.Status == EmailOutboxStatus.Pending || x.Status == EmailOutboxStatus.Failed)
                && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt)
            .Select(x => x.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var claimed = new List<EmailOutboxItem>(dueIds.Count);

        foreach (var id in dueIds)
        {
            var updated = await db.EmailOutbox
                .Where(x =>
                    x.Id == id
                    && (x.Status == EmailOutboxStatus.Pending || x.Status == EmailOutboxStatus.Failed))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, EmailOutboxStatus.Processing)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false);

            if (updated == 0)
                continue;

            var item = await db.EmailOutbox
                .AsNoTracking()
                .FirstAsync(x => x.Id == id, cancellationToken)
                .ConfigureAwait(false);

            claimed.Add(item);
        }

        return claimed;
    }

    private async Task ProcessItemAsync(
        EmailOutboxItem item, SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        try
        {
            var sendResult = await smtp
                .SendEmailAsync(item.To, item.Subject, item.Body, item.IsHtml, cancellationToken)
                .ConfigureAwait(false);

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var now = DateTime.UtcNow;

            if (sendResult.IsSuccess)
            {
                await db.EmailOutbox
                    .Where(x => x.Id == item.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, EmailOutboxStatus.Sent)
                        .SetProperty(x => x.UpdatedAt, now), cancellationToken)
                    .ConfigureAwait(false);

                metrics.RecordSent();
                return;
            }

            await HandleFailureAsync(
                    db,
                    item,
                    sendResult.ErrorMessage ?? "邮件发送失败",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "邮件 Outbox 处理异常 Id={OutboxId} To={To}", item.Id, item.To);

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await HandleFailureAsync(db, item, ex.Message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task HandleFailureAsync(
        UserDbContext db,
        EmailOutboxItem item,
        string error,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var attemptCount = item.AttemptCount + 1;
        var truncatedError = error.Length <= 2048 ? error : error[..2048];

        if (attemptCount >= MaxAttempts)
        {
            await db.EmailOutbox
                .Where(x => x.Id == item.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, EmailOutboxStatus.Dead)
                    .SetProperty(x => x.AttemptCount, attemptCount)
                    .SetProperty(x => x.LastError, truncatedError)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false);

            metrics.RecordDead();
            return;
        }

        var nextAttemptAt = now.Add(CalculateBackoff(attemptCount));

        await db.EmailOutbox
            .Where(x => x.Id == item.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, EmailOutboxStatus.Failed)
                .SetProperty(x => x.AttemptCount, attemptCount)
                .SetProperty(x => x.LastError, truncatedError)
                .SetProperty(x => x.NextAttemptAt, nextAttemptAt)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);

        metrics.RecordFailed();
    }

    private static TimeSpan CalculateBackoff(int attemptCount)
    {
        var delaySeconds = Math.Min(3600, Math.Pow(2, attemptCount - 1) * 30);
        return TimeSpan.FromSeconds(delaySeconds);
    }
}
