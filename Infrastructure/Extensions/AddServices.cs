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
        services.AddScoped<IMfaService, MfaService>();
        services.AddScoped<ISecurityNotificationService, SecurityNotificationService>();
        services.AddScoped<IAdminAuditQuery, AdminAuditQuery>();
        services.AddSingleton<IMessageEvidenceProvider, UnavailableMessageEvidenceProvider>();
        services.AddScoped<IModerationService, ModerationService>();
        services.AddScoped<IAccountLifecycleService, AccountLifecycleService>();
        services.AddScoped<INotificationQuery, NotificationQuery>();
        services.AddScoped<ITrustedDeviceService, TrustedDeviceService>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserAccountService, UserAccountService>();
        services.AddScoped<IFriendshipService, FriendshipService>();
        services.AddScoped<ISecurityEventStore, SecurityEventStore>();
        services.AddSingleton<AvatarReencodeQueue>();
        services.AddHostedService<AvatarCleanupWorker>();
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