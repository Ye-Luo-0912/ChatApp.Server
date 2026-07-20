using Core.Interfaces;
using Core.Models.Email;

namespace Core.Interfaces;

public interface IEmailSender
{
    Task<EmailResult> SendEmailAsync(
        string to,
        string subject,
        string body,
        bool isHtml = true,
        CancellationToken cancellation = default);

    /// <summary>
    /// 入队邮件（支持类型与幂等键，防止验证码等重复入队）。
    /// </summary>
    Task<EmailResult> EnqueueEmailAsync(
        string to,
        string subject,
        string body,
        bool isHtml = true,
        string? emailType = null,
        string? idempotencyKey = null,
        CancellationToken cancellation = default);

    Task<EmailResult> SendVerificationEmailAsync(
        string to, string username, string verificationToken, CancellationToken cancellation);
}
