using System.Net;
using Core.Interfaces;
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
    : IEmailSender
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
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

            await smtp.ConnectAsync(
                    _emailConfig.Host,
                    _emailConfig.Port,
                    SecureSocketOptions.SslOnConnect,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            await smtp.AuthenticateAsync(
                    _emailConfig.SenderEmail,
                    _emailConfig.Password,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            await smtp.SendAsync(email, timeoutCts.Token).ConfigureAwait(false);
            await smtp.DisconnectAsync(true, timeoutCts.Token).ConfigureAwait(false);

            return new EmailResult { IsSuccess = true };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "发送邮件失败 To={To}", to);
            return new EmailResult { IsSuccess = false, ErrorMessage = "邮件发送失败" };
        }
    }

    public Task<EmailResult> SendVerificationEmailAsync(
        string to, string username, string verificationToken, CancellationToken cancellation)
    {
        var safeUserName = WebUtility.HtmlEncode(username);
        var subject = "【ChatApp】请验证您的注册邮箱";
        var htmlBody = $@"
            <div style='font-family: Arial, sans-serif; padding: 20px; color: #333;'>
                <h2 style='color: #1E293B;'>您好, {safeUserName}</h2>
                <p>感谢您注册 ChatApp。您的专属验证码是：</p>
                <div style='font-size: 28px; font-weight: bold; color: #3B82F6; padding: 12px 24px; background: #F0FDF4; display: inline-block; border-radius: 8px; letter-spacing: 4px;'>
                    {verificationToken}
                </div>
                <p style='color: #64748B; font-size: 13px; margin-top: 20px;'>本验证码 5 分钟内有效。如非本人操作，请忽略此邮件。</p>
            </div>";

        return SendEmailAsync(to, subject, htmlBody, true, cancellation);
    }
}
