using Core.Interfaces;
using Core.Models.Email;

namespace Infrastructure.Services.Email;

/// <summary>
/// API 侧邮件发送门面：入队后等待后台 worker 完成（带超时）。
/// </summary>
public sealed class QueuedEmailSender(EmailQueue queue) : IEmailSender
{
    private static readonly TimeSpan EnqueueWaitTimeout = TimeSpan.FromSeconds(25);

    public async Task<EmailResult> SendEmailAsync(
        string to, string subject, string body, bool isHtml = true, CancellationToken cancellation = default)
    {
        var tcs = new TaskCompletionSource<EmailResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new EmailWorkItem(to, subject, body, isHtml, tcs);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            cts.CancelAfter(EnqueueWaitTimeout);
            await queue.EnqueueAsync(item, cts.Token).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new EmailResult { IsSuccess = false, ErrorMessage = "邮件队列繁忙或发送超时" };
        }
    }

    public Task<EmailResult> SendVerificationEmailAsync(
        string to, string username, string verificationToken, CancellationToken cancellation)
        => SendEmailAsync(to, "verify", verificationToken, true, cancellation);
}
