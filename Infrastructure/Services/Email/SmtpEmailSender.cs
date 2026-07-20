using Core.Models.Email;
using Infrastructure.Data.Configurations;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;

namespace Infrastructure.Services.Email;

/// <summary>
/// 同步 SMTP 发送实现；由后台队列调用，不直接暴露给 API 请求路径。
/// </summary>
public sealed class SmtpEmailSender(IOptions<EmailConfig> emailConfigOptions, ILogger<SmtpEmailSender> logger)
{
    private readonly EmailConfig _emailConfig = emailConfigOptions.Value;

    public async Task<EmailResult> SendEmailAsync(
        string to, string subject, string body, bool isHtml = true, CancellationToken cancellation = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(to)
                || string.IsNullOrWhiteSpace(subject)
                || string.IsNullOrWhiteSpace(body))
            {
                return new EmailResult { IsSuccess = false, ErrorMessage = "收件人、主题和内容不能为空" };
            }

            logger.LogDebug("准备发送邮件 Host={Host} Port={Port} To={To}",
                _emailConfig.Host, _emailConfig.Port, to);

            using var email = new MimeMessage();
            email.From.Add(new MailboxAddress(_emailConfig.SenderName, _emailConfig.SenderEmail));
            email.To.Add(MailboxAddress.Parse(to));
            email.Subject = subject;
            email.Body = new TextPart(isHtml ? TextFormat.Html : TextFormat.Plain) { Text = body };

            using var smtp = new SmtpClient { Timeout = 15_000 };
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeoutCts.Token);

            await smtp.ConnectAsync(
                    _emailConfig.Host,
                    _emailConfig.Port,
                    SecureSocketOptions.SslOnConnect,
                    linkedCts.Token)
                .ConfigureAwait(false);

            await smtp.AuthenticateAsync(
                    _emailConfig.SenderEmail,
                    _emailConfig.Password,
                    linkedCts.Token)
                .ConfigureAwait(false);

            await smtp.SendAsync(email, linkedCts.Token).ConfigureAwait(false);
            await smtp.DisconnectAsync(true, linkedCts.Token).ConfigureAwait(false);

            return new EmailResult { IsSuccess = true };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "发送邮件失败 To={To}", to);
            return new EmailResult { IsSuccess = false, ErrorMessage = "邮件发送失败" };
        }
    }
}
