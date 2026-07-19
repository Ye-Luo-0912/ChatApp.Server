using Core.Models;
using Core.Models.Email;

namespace Core.Interfaces
{
    public interface IEmailVerificationService
    {
        /// <summary>
        /// 发送邮箱验证码。
        /// </summary>
        /// <remarks>
        /// 冷静期内不允许重复发送；过了冷静期但验证码仍有效时，直接重发原验证码，不刷新 TTL。
        /// </remarks>
        /// <param name="email">接收验证码的电子邮件地址。</param>
        /// <param name="codePurpose">验证码用途，如注册、重置密码等。</param>
        /// <param name="cancellation">用于请求取消操作的令牌。</param>
        /// <returns>返回一个EmailResult对象，包含操作是否成功以及错误信息。</returns>
        Task<EmailResult> SendEmailCodeAsync(string email, EmailCodePurpose codePurpose,
            CancellationToken cancellation);

        /// <summary>
        /// 验证提供的电子邮件验证码是否有效。
        /// </summary>
        /// <param name="email">接收验证码的电子邮件地址。</param>
        /// <param name="code">用户输入的验证码。</param>
        /// <param name="codePurpose">验证码用途，如注册、重置密码等。</param>
        /// <param name="cancellation">用于请求取消操作的令牌。</param>
        /// <returns>返回一个EmailResult对象，包含验证结果以及错误信息（如果有的话）。</returns>
        Task<EmailResult> VerifyEmailCodeAsync(string email, string code, EmailCodePurpose codePurpose,
            CancellationToken cancellation);
        
    }
}
