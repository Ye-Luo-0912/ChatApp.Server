namespace Infrastructure.Models;

/// <summary>
/// 用于封装发送电子邮件验证码请求的参数。此请求包含接收者的电子邮件地址以及发送验证码的目的。
/// </summary>
public class SendEmailCodeRequest
{
    public required string Email { get; set; }
    
}