using Core.Interfaces;
using Core.Settings;
using Infrastructure.Data.Configurations;
using Infrastructure.Services;
using Infrastructure.Services.Email;
using Infrastructure.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Infrastructure.Extensions;

/// <summary>
/// 通知与邮件模块的组合根：通知出箱 Worker、邮件出箱/发送、邮箱验证、
/// EmailConfig 启动校验及 NotificationOutboxOptions 校验。
/// </summary>
public static class NotificationModuleExtensions
{
    /// <summary>注册通知与邮件模块。</summary>
    public static IServiceCollection AddNotificationModule(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<NotificationOutboxOptions>(config.GetSection(NotificationOutboxOptions.SectionName));

        services.AddSingleton<NotificationOutboxMetrics>();
        services.AddScoped<INotificationQuery, NotificationQuery>();
        services.AddHostedService<NotificationDispatchWorker>();

        services.AddSingleton<EmailOutboxMetrics>();
        services.AddSingleton<SmtpEmailSender>();
        services.AddSingleton<IEmailSender, QueuedEmailSender>();
        services.AddHostedService<EmailDispatchWorker>();
        services.AddSingleton<IEmailVerificationService, EmailVerificationService>();

        services.Configure<EmailConfig>(config.GetSection("EmailSettings"))
            .AddOptions<EmailConfig>()
            .Validate<IHostEnvironment>(
                (options, environment) =>
                    environment.IsDevelopment()
                    || environment.IsEnvironment("Testing")
                    || (!string.IsNullOrWhiteSpace(options.Host)
                        && !string.IsNullOrWhiteSpace(options.SenderEmail)
                        && !string.IsNullOrWhiteSpace(options.Password)),
                "生产环境必须完整配置 EmailSettings:Host、SenderEmail、Password")
            .Validate(s => s.Port is > 0 and <= 65535, "EmailSettings:Port 必须在 1-65535 之间")
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<NotificationOutboxOptions>, NotificationOutboxOptionsValidator>();

        return services;
    }
}
