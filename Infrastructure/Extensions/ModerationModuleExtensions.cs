using ChatApp.Realtime.Integration;
using Core.Interfaces;
using Core.Settings;
using Infrastructure.Services;
using Infrastructure.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.Extensions;

/// <summary>
/// 内容审核模块的组合根：消息证据提供者（Realtime 或不可用占位）与审核服务，
/// 以及 MessageEvidenceOptions 绑定与校验。
/// </summary>
public static class ModerationModuleExtensions
{
    /// <summary>注册内容审核模块。</summary>
    public static IServiceCollection AddModerationModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddValidatedOptions<MessageEvidenceOptions, MessageEvidenceOptionsValidator>(
            config, MessageEvidenceOptions.SectionName);

        services.AddSingleton<IMessageEvidenceProvider>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<MessageEvidenceOptions>>().Value;
            var bus = sp.GetService<IRealtimeMessageBus>();
            if (!string.IsNullOrWhiteSpace(opts.RealtimeConnectionString) || bus is not null)
            {
                return new RealtimeMessageEvidenceProvider(
                    sp.GetRequiredService<IOptions<MessageEvidenceOptions>>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RealtimeMessageEvidenceProvider>>(),
                    bus);
            }

            return ActivatorUtilities.CreateInstance<UnavailableMessageEvidenceProvider>(sp);
        });
        services.AddScoped<IModerationService, ModerationService>();

        return services;
    }
}
