using Core.Models.DTOs;

namespace Core.Interfaces;

public interface IEmailSender
{
    /// <summary>
    /// 发送邮件消息
    /// </summary>
    /// <param name="to">收件人邮箱地址</param>
    /// <param name="subject">邮件主题</param>
    /// <param name="body">邮件正文 (HTML 格式)</param>
    /// <param name="isHtml">是否为 HTML 邮件 (默认为 true)</param>
    /// <param name="cancellation">取消令牌，用于支持操作的取消</param>
    /// <returns>一个包含发送结果的对象，包括是否成功和错误信息</returns>
    Task<EmailResult> SendEmailAsync(string to, string subject, string body, bool isHtml = true,
        CancellationToken cancellation = default);

    /// <summary>
    /// 发送带验证令牌的邮件
    /// </summary>
    /// <param name="to">收件人邮箱地址</param>
    /// <param name="username">用户名，用于个性化邮件内容</param>
    /// <param name="verificationToken">用于验证的令牌</param>
    /// <param name="cancellation">取消令牌，用于支持操作的取消</param>
    /// <returns>一个包含发送结果的对象，包括是否成功和错误信息</returns>
    Task<EmailResult> SendVerificationEmailAsync(string to, string username, string verificationToken,
        CancellationToken cancellation = default);
}