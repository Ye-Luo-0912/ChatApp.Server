using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Email;

/// <summary>
/// 有界并发的邮件后台发送器。
/// </summary>
public sealed class EmailDispatchWorker(
    EmailQueue queue,
    SmtpEmailSender smtp,
    ILogger<EmailDispatchWorker> logger) : BackgroundService
{
    private const int MaxConcurrency = 4;
    private const int MaxAttempts = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable
            .Range(0, MaxConcurrency)
            .Select(_ => RunWorkerAsync(stoppingToken));

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in queue.ReadAllAsync(stoppingToken))
        {
            EmailResultOrError result;
            try
            {
                result = await SendWithRetryAsync(item, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "邮件发送 worker 异常 To={To}", item.To);
                result = new EmailResultOrError(false, "邮件发送失败");
            }

            item.Completion.TrySetResult(new Core.Models.Email.EmailResult
            {
                IsSuccess = result.Success,
                ErrorMessage = result.Error,
            });
        }
    }

    private async Task<EmailResultOrError> SendWithRetryAsync(EmailWorkItem item, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var send = await smtp
                .SendEmailAsync(item.To, item.Subject, item.Body, item.IsHtml, ct)
                .ConfigureAwait(false);

            if (send.IsSuccess)
                return new EmailResultOrError(true, null);

            if (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct).ConfigureAwait(false);
                continue;
            }

            return new EmailResultOrError(false, send.ErrorMessage ?? "邮件发送失败");
        }

        return new EmailResultOrError(false, "邮件发送失败");
    }

    private readonly record struct EmailResultOrError(bool Success, string? Error);
}
