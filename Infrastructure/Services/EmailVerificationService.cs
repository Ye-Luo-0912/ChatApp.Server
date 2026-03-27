using System.Security.Cryptography;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Models;
using Core.Models.DTOs;

namespace Infrastructure.Services
{
    /// <summary>
    /// 负责邮箱验证码的发送与校验，并约束同一邮箱同一用途下的重发规则。
    /// </summary>
    public class EmailVerificationService : IEmailVerificationService
    {
        /// <summary>
        /// 验证码总有效期。
        /// </summary>
        private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

        /// <summary>
        /// 两次发送之间的最短等待时间。
        /// </summary>
        private static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);

        private readonly IEmailSender _emailSender;
        private readonly ICacheProvider _cacheProvider;

        public EmailVerificationService(IEmailSender emailSender, ICacheProvider cache)
        {
            _emailSender = emailSender;
            _cacheProvider = cache;
        }

        /// <summary>
        /// 发送邮箱验证码。
        /// </summary>
        /// <remarks>
        /// 冷静期内不允许重复发送；过了冷静期但验证码仍有效时，直接重发原验证码，不刷新 TTL。
        /// </remarks>
        public async Task<EmailResult> SendEmailCodeAsync(string email, EmailCodePurpose codePurpose, CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(email))
                return new EmailResult { IsSuccess = false, ErrorMessage = "邮箱不能为空" };

            try
            {
                var normalizedEmail = NormalizeEmail(email);
                var dataKey = $"EmailCode:{codePurpose}:{normalizedEmail}";
                var cooldownKey = $"EmailCooldown:{codePurpose}:{normalizedEmail}";
                
                var isCoolingDown = await _cacheProvider.ExistsAsync(cooldownKey).ConfigureAwait(false);
                if (isCoolingDown)
                {
                    // 直接返回通用提示即可，前端通常自己有 60 秒倒计时。
                    return new EmailResult { IsSuccess = false, ErrorMessage = "操作太频繁，请稍后再试" };
                }
                
                //检查是否有存活的旧验证码
                var cachedCode = await _cacheProvider.StringGetAsync(dataKey, cancellationToken: cancellation).ConfigureAwait(false);
                string codeToSend;
                
                if (!string.IsNullOrEmpty(cachedCode))
                {
                    codeToSend = cachedCode;
                }
                else
                {
                    // 没有旧的，生成新的，并存入 5 分钟生命周期
                    codeToSend = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
                    await _cacheProvider.StringSetAsync(dataKey, codeToSend, absoluteExpiration: CodeLifetime, cancellationToken: cancellation).ConfigureAwait(false);
                }
                
                var sendResult = await SendVerificationEmailAsync(NormalizeEmail(email), codeToSend, codePurpose, cancellation).ConfigureAwait(false);
        
                if (sendResult.IsSuccess)
                {
                    // 只要发送成功，立刻给这个邮箱上 60 秒的“绝对禁言套餐”！
                    await _cacheProvider.StringSetAsync(cooldownKey, "locked", absoluteExpiration: ResendCooldown, cancellationToken: cancellation).ConfigureAwait(false);
                }
                else if (string.IsNullOrEmpty(cachedCode))
                {
                    // 如果是新生成的码且发送失败，清理掉，避免占坑
                    await _cacheProvider.RemoveAsync(dataKey, cancellation).ConfigureAwait(false);
                }

                return sendResult;
            }
            catch (Exception ex)
            {
                return new EmailResult
                {
                    IsSuccess = false,
                    ErrorMessage = $"验证码发送失败: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 验证提供的邮箱验证码是否正确。
        /// </summary>
        /// <param name="email">需要验证的电子邮件地址。</param>
        /// <param name="code">用户输入的验证码。</param>
        /// <param name="codePurpose">验证码的用途，例如注册、重置密码等。</param>
        /// <param name="cancellation">用于取消异步操作的令牌。</param>
        /// <returns>返回一个EmailResult对象，包含验证结果和错误信息（如果有的话）。</returns>
        public async Task<EmailResult> VerifyEmailCodeAsync(string email, string code, EmailCodePurpose codePurpose,
            CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(code))
                return new EmailResult { IsSuccess = false, ErrorMessage = "验证码或者邮箱不能为空" };

            var normalizedCode = code.Trim();
            var cacheKey = GetCacheKey(email, codePurpose);
            var savedCode = await _cacheProvider.StringGetAsync(cacheKey, cancellationToken: cancellation).ConfigureAwait(false);

            if (string.IsNullOrEmpty(savedCode))
            {
                return new EmailResult { IsSuccess = false, ErrorMessage = "验证码已过期或尚未发送" };
            }

            if (!string.Equals(savedCode, normalizedCode, StringComparison.Ordinal))
            {
                return new EmailResult { IsSuccess = false, ErrorMessage = "验证码错误" };
            }

            // 验证成功后立即删除，避免同一验证码被重复使用。
            await _cacheProvider.RemoveAsync(cacheKey, cancellation).ConfigureAwait(false);
            return new EmailResult { IsSuccess = true };
        }

        /// <summary>
        /// 生成验证码缓存键，确保同一邮箱和用途只对应一条有效验证码。
        /// </summary>
        private static string GetCacheKey(string email, EmailCodePurpose purpose) => $"EmailCode:{purpose}:{NormalizeEmail(email)}";

        /// <summary>
        /// 对邮箱做统一归一化，避免大小写和首尾空格导致重复键。
        /// </summary>
        private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

        /// <summary>
        /// 发送验证电子邮件。
        /// </summary>
        /// <param name="email">接收验证码的电子邮件地址。</param>
        /// <param name="code">要发送的验证码。</param>
        /// <param name="codePurpose">验证码的用途，如注册、重置密码等。</param>
        /// <param name="cancellation">用于请求取消操作的令牌。</param>
        /// <returns>表示电子邮件发送结果的对象。</returns>
        private async Task<EmailResult> SendVerificationEmailAsync(string email, string code,
            EmailCodePurpose codePurpose, CancellationToken cancellation)
        {
            var purposeText = GetPurposeText(codePurpose);
            var subject = $"【ChatApp】{purposeText}验证码";
            var body =
                "<div style='padding: 20px; font-family: sans-serif;'>" +
                $"<h2>您正在进行{purposeText}操作</h2>" +
                $"<p>您的验证码是：<strong style='font-size: 24px; color: #3B82F6;'>{code}</strong></p>" +
                "<p style='color: #666; font-size: 12px;'>验证码 5 分钟内有效，请勿泄露给他人。</p>" +
                "</div>";

            return await _emailSender.SendEmailAsync(email, subject, body, isHtml: true, cancellation: cancellation).ConfigureAwait(false);
        }

        /// <summary>
        /// 将用途枚举转换成邮件中展示的中文说明。
        /// </summary>
        private static string GetPurposeText(EmailCodePurpose purpose) => purpose switch
        {
            EmailCodePurpose.Register => "注册账号",
            EmailCodePurpose.ResetPassword => "重置密码",
            EmailCodePurpose.BindEmail => "绑定邮箱",
            _ => "身份验证"
        };
    }
}