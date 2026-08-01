using ChatApp.Realtime.Integration;
using ChatApp.Realtime.Integration.Configuration;
using ChatApp.Realtime.Integration.DependencyInjection;
using Core.Settings;
using Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Extensions;

/// <summary>
/// Realtime 集成模块的组合根：RealtimeGateway / RealtimeIntegration 选项绑定与校验、
/// Realtime 出箱管理、Presence 授权 Worker，以及按需接入 NATS Realtime 总线。
/// </summary>
public static class RealtimeModuleExtensions
{
    /// <summary>注册 Realtime 集成模块。</summary>
    public static IServiceCollection AddRealtimeIntegrationModule(this IServiceCollection services, IConfiguration config,
        bool registerWorkerHostedServices)
    {
        services.Configure<RealtimeGatewayOptions>(config.GetSection(RealtimeGatewayOptions.SectionName))
            .AddOptions<RealtimeGatewayOptions>()
            .Validate(s => !string.IsNullOrWhiteSpace(s.Host), "RealtimeGateway:Host 必填")
            .Validate(s => s.Port > 0, "RealtimeGateway:Port 必须 > 0")
            .ValidateOnStart();

        services.Configure<RealtimeIntegrationHostOptions>(config.GetSection(RealtimeIntegrationHostOptions.SectionName));

        services.AddScoped<IRealtimeOutboxAdminService, RealtimeOutboxAdminService>();
        if (registerWorkerHostedServices)
        {
            services.AddHostedService(sp => new PresenceAuthorizeWorker(
                sp.GetService<IRealtimeMessageBus>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IOptions<MessageEvidenceOptions>>(),
                sp.GetRequiredService<IOptions<DataExportStorageOptions>>(),
                sp.GetRequiredService<ILogger<PresenceAuthorizeWorker>>()));
        }

        TryAddRealtimeIntegration(services, config);

        return services;
    }

    private static void TryAddRealtimeIntegration(IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection(RealtimeIntegrationHostOptions.SectionName);
        var hostOpts = section.Get<RealtimeIntegrationHostOptions>() ?? new RealtimeIntegrationHostOptions();
        if (string.IsNullOrWhiteSpace(hostOpts.Url))
            return;

        services.AddChatAppRealtimeIntegration(new RealtimeIntegrationOptions
        {
            Url = hostOpts.Url,
            ClientName = string.IsNullOrWhiteSpace(hostOpts.ClientName) ? "chatapp-server" : hostOpts.ClientName,
            InstanceId = string.IsNullOrWhiteSpace(hostOpts.InstanceId)
                ? Environment.MachineName
                : hostOpts.InstanceId,
            AccountCleanupSubject = hostOpts.AccountCleanupSubject,
            AccountCleanupConsumerName = hostOpts.AccountCleanupConsumerName,
            RealtimeEventsSubject = hostOpts.RealtimeEventsSubject,
            RealtimeEventsStream = hostOpts.RealtimeEventsStream,
            DeadLettersSubject = hostOpts.DeadLettersSubject,
            DeadLettersStream = hostOpts.DeadLettersStream,
            ManageStreams = false,
        });
    }
}
