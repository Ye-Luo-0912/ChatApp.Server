using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Services;
using Infrastructure.Diagnostics;
using Infrastructure.Services;
using Infrastructure.Services.Auth;
using Infrastructure.Services.Email;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Extensions;

/// <summary>
/// 注册基础设施层和核心业务层所需的服务。
/// </summary>
public static class AddServices
{
    /// <summary>
    /// 注册当前项目使用到的核心服务。
    /// </summary>
    public static void AddCoreServiceCollection(this IServiceCollection services)
    {
        services.AddSingleton<IDeviceInfo, DeviceInfoService>();
        services.AddSingleton<IAuthCpuLimiter, AuthCpuLimiter>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<NotificationOutboxMetrics>();
        services.AddSingleton<AvatarReencodeMetrics>();

        // TokenService 是单例，同时以多个子接口注册，各处可按需注入
        services.AddSingleton<TokenService>();
        services.AddSingleton<ITokenService>(sp => sp.GetRequiredService<TokenService>());
        // OpaqueTokenAuthHandler 仅依赖 IAccessTokenStore，单独注册以减小耦合
        services.AddSingleton<IAccessTokenStore>(sp => sp.GetRequiredService<TokenService>());
        services.AddSingleton<ISessionStore>(sp => sp.GetRequiredService<TokenService>());
        services.AddSingleton<IRefreshTokenStore>(sp => sp.GetRequiredService<TokenService>());

        services.AddScoped<IAuthService, AuthService>();
        services.AddSingleton<IMfaSecretProtector, AesGcmMfaSecretProtector>();
        services.AddSingleton<IRecoveryCodeHasher, HmacRecoveryCodeHasher>();
        services.AddScoped<IMfaService, MfaService>();
        services.AddScoped<ISecurityNotificationService, SecurityNotificationService>();
        services.AddScoped<IAdminAuditQuery, AdminAuditQuery>();
        services.AddSingleton<IMessageEvidenceProvider>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Settings.MessageEvidenceOptions>>().Value;
            var bus = sp.GetService<ChatApp.Realtime.Integration.IRealtimeMessageBus>();
            if (!string.IsNullOrWhiteSpace(opts.RealtimeConnectionString) || bus is not null)
            {
                return new RealtimeMessageEvidenceProvider(
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Settings.MessageEvidenceOptions>>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RealtimeMessageEvidenceProvider>>(),
                    bus);
            }

            return ActivatorUtilities.CreateInstance<UnavailableMessageEvidenceProvider>(sp);
        });
        services.AddScoped<IModerationService, ModerationService>();
        services.AddScoped<IAccountLifecycleService, AccountLifecycleService>();
        services.AddScoped<AccountCleanupSagaService>();
        services.AddScoped<IAccountCleanupSagaService>(sp => sp.GetRequiredService<AccountCleanupSagaService>());
        services.AddScoped<IAttachmentBlobDeleteService, AttachmentBlobDeleteService>();
        services.AddSingleton<AttachmentBlobDeleteEnqueuer>();
        services.AddHostedService(sp => new AccountCleanupSagaWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetService<ChatApp.Realtime.Integration.IRealtimeMessageBus>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Settings.AccountCleanupSagaOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AccountCleanupSagaWorker>>()));
        services.AddScoped<INotificationQuery, NotificationQuery>();
        services.AddScoped<ITrustedDeviceService, TrustedDeviceService>();
        services.AddScoped<IRealtimeOutboxAdminService, RealtimeOutboxAdminService>();
        services.AddSingleton<LoginRiskAnalyzer>();
        services.AddSingleton<ILoginRiskAnalyzer>(sp => sp.GetRequiredService<LoginRiskAnalyzer>());
        services.AddHostedService(sp => sp.GetRequiredService<LoginRiskAnalyzer>());
        services.AddSingleton<IDataExportBlobStore, LocalDataExportBlobStore>();
        services.AddSingleton<IRealtimeChatExportReader>(sp =>
        {
            var evidence = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Settings.MessageEvidenceOptions>>().Value;
            var export = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Settings.DataExportStorageOptions>>().Value;
            var bus = sp.GetService<ChatApp.Realtime.Integration.IRealtimeMessageBus>();
            if (!string.IsNullOrWhiteSpace(export.RealtimeConnectionString)
                || !string.IsNullOrWhiteSpace(evidence.RealtimeConnectionString)
                || bus is not null)
            {
                return new RealtimeChatExportReader(
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Settings.MessageEvidenceOptions>>(),
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Settings.DataExportStorageOptions>>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RealtimeChatExportReader>>(),
                    bus);
            }

            return new UnavailableRealtimeChatExportReader();
        });
        services.AddScoped<IDataExportService, DataExportService>();
        services.AddHostedService<DataExportWorker>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserAccountService, UserAccountService>();
        services.AddScoped<IFriendshipService, FriendshipService>();
        services.AddScoped<ISecurityEventStore, SecurityEventStore>();
        services.AddSingleton<AvatarReencodeQueue>();
        services.AddHostedService<AvatarCleanupWorker>();
        services.AddHostedService<AttachmentCleanupWorker>();
        services.AddHostedService<SecurityEventArchiveWorker>();
        services.AddHostedService<AccountDeletionWorker>();
        services.AddHostedService<NotificationDispatchWorker>();
        services.AddHostedService<RuntimeHealthMetrics>();

        services.AddSingleton<IAvatarStorage>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Settings.AvatarStorageOptions>>().Value;
            if (string.Equals(opts.Provider, "S3", StringComparison.OrdinalIgnoreCase))
                return ActivatorUtilities.CreateInstance<S3AvatarStorage>(sp);
            return ActivatorUtilities.CreateInstance<LocalAvatarStorage>(sp);
        });

        services.AddSingleton<IAttachmentContentScanner, DenyListAttachmentContentScanner>();
        services.AddSingleton<IAttachmentStorage>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Settings.AttachmentStorageOptions>>().Value;
            if (string.Equals(opts.Provider, "S3", StringComparison.OrdinalIgnoreCase))
                return ActivatorUtilities.CreateInstance<S3AttachmentStorage>(sp);
            return ActivatorUtilities.CreateInstance<LocalAttachmentStorage>(sp);
        });
        services.AddSingleton<IAttachmentMetadataStore>(sp =>
        {
            var evidence = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Settings.MessageEvidenceOptions>>().Value;
            var export = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Settings.DataExportStorageOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(export.RealtimeConnectionString)
                || !string.IsNullOrWhiteSpace(evidence.RealtimeConnectionString))
            {
                return new RealtimeAttachmentMetadataStore(
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Settings.MessageEvidenceOptions>>(),
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Settings.DataExportStorageOptions>>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RealtimeAttachmentMetadataStore>>());
            }

            return new UnavailableAttachmentMetadataStore();
        });
        services.AddScoped<IAttachmentService, AttachmentService>();

        // 使用命名 HttpClient，IHttpClientFactory 管理连接池，避免套接字耗尽
        services.AddHttpClient(nameof(GeoLocationService), client =>
        {
            client.BaseAddress = new Uri("http://ip-api.com/");
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });
        services.AddSingleton<IGeoLocationService, GeoLocationService>();

        services.AddSingleton<EmailOutboxMetrics>();
        services.AddSingleton<SmtpEmailSender>();
        services.AddSingleton<IEmailSender, QueuedEmailSender>();
        services.AddHostedService<EmailDispatchWorker>();
        services.AddSingleton<IEmailVerificationService, EmailVerificationService>();
    }
}