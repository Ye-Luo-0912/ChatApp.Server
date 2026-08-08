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
    public static IServiceCollection AddNotificationModule(this IServiceCollection services, IConfiguration config,
        bool registerWorkerHostedServices)
    {
        services.AddValidatedOptions<NotificationOutboxOptions, NotificationOutboxOptionsValidator>(
            config, NotificationOutboxOptions.SectionName);

        services.AddSingleton<NotificationOutboxMetrics>();
        services.AddSingleton<NotificationOutboxJobStore>();
        services.AddSingleton<ILeasedJobStore<Core.Models.Notifications.NotificationOutboxItem>>(sp =>
            sp.GetRequiredService<NotificationOutboxJobStore>());
        services.AddScoped<INotificationQuery, NotificationQuery>();
        if (registerWorkerHostedServices)
            services.AddHostedService<NotificationDispatchWorker>();

        services.AddSingleton<EmailOutboxMetrics>();
        services.AddScoped<IEmailOutboxAdminService, EmailOutboxAdminService>();
        services.AddSingleton<SmtpEmailSender>();
        services.AddSingleton<IEmailSender, QueuedEmailSender>();
        services.AddSingleton<EmailOutboxJobStore>();
        services.AddSingleton<ILeasedJobStore<Core.Models.Email.EmailOutboxItem>>(sp =>
            sp.GetRequiredService<EmailOutboxJobStore>());
        if (registerWorkerHostedServices)
            services.AddHostedService<EmailDispatchWorker>();
        services.AddSingleton<IEmailVerificationService, EmailVerificationService>();
        services.AddHttpClient("phone-verification", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });
        services.AddOptions<PhoneVerificationOptions>()
            .Bind(config.GetSection(PhoneVerificationOptions.SectionName))
            .Validate(s => s.CodeLifetimeMinutes is >= 1 and <= 15,
                "PhoneVerification:CodeLifetimeMinutes 必须在 1..15")
            .Validate(s => s.ResendCooldownSeconds is >= 10 and <= 300,
                "PhoneVerification:ResendCooldownSeconds 必须在 10..300")
            .Validate<IHostEnvironment>((s, env) =>
                env.IsDevelopment() || env.IsEnvironment("Testing") ||
                (!string.IsNullOrWhiteSpace(s.WebhookUrl)
                 && Uri.TryCreate(s.WebhookUrl, UriKind.Absolute, out var uri)
                 && uri.Scheme == Uri.UriSchemeHttps),
                "生产环境必须配置 HTTPS PhoneVerification:WebhookUrl")
            .ValidateOnStart();
        services.AddSingleton<IPhoneVerificationSender, PhoneVerificationSender>();
        services.AddSingleton<IPhoneVerificationService, PhoneVerificationService>();

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

        return services;
    }
}
