using System.Net;
using Core.Interfaces;
using Core.Models.DTOs;
using Infrastructure.Models.Config;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;

namespace Infrastructure.Services;

/// <summary>
/// 处理邮件发送功能
/// </summary>
public class EmailService(IOptions<EmailConfig> emailConfigOptions, ILogger<EmailService> logger) : IEmailSender
{

    private readonly ILogger<EmailService> _logger = logger;
    private readonly EmailConfig _emailConfig = emailConfigOptions.Value;



    /// <summary>
    /// 发送邮件消息
    /// </summary>
    public async Task<EmailResult> SendEmailAsync(string to, string subject, string body, bool isHtml = true, CancellationToken cancellation = default)
    {
        try
        {
            if(string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
            {
                return new EmailResult { IsSuccess = false, ErrorMessage = "收件人、主题和内容不能为空" };
            }

            _logger.LogDebug("Email配置： ;{host}, {pwd}, {port}", _emailConfig.Host,_emailConfig.Password, _emailConfig.Port);
            
            using var email = new MimeMessage();

            email.From.Add(new MailboxAddress(_emailConfig.SenderName, _emailConfig.SenderEmail));
            email.To.Add(MailboxAddress.Parse(to));
            email.Subject = subject;
            email.Body = new TextPart(isHtml ? TextFormat.Html : TextFormat.Plain) { Text = body };

            using var smtp = new SmtpClient();

            // 连接 SMTP 服务器
            await smtp.ConnectAsync(_emailConfig.Host, _emailConfig.Port, SecureSocketOptions.SslOnConnect, cancellation).ConfigureAwait(false);
            // 登录鉴权
            await smtp.AuthenticateAsync(_emailConfig.SenderEmail, _emailConfig.Password, cancellation).ConfigureAwait(false);
            // 发送邮件

            await smtp.SendAsync(email, cancellation).ConfigureAwait(false);
            await smtp.DisconnectAsync(true, cancellation).ConfigureAwait(false);
            
            return new EmailResult { IsSuccess = true };
        }
        catch (Exception ex)
        {
            //
            _logger.LogError("发送邮件失败: {Message}", ex.Message);
            return new EmailResult { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// 发送带验证令牌的邮件
    /// </summary>
    public async Task<EmailResult> SendVerificationEmailAsync(string to, string username, string verificationToken, CancellationToken cancellation)
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

       return await SendEmailAsync(to, subject, htmlBody, true, cancellation).ConfigureAwait(false);
    }
}