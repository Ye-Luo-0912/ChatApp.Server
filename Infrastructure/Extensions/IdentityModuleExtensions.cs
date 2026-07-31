using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Settings;
using Infrastructure.Auth;
using Infrastructure.Services;
using Infrastructure.Services.Auth;
using Infrastructure.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Infrastructure.Extensions;

/// <summary>
/// 身份认证与会话相关服务的组合根模块：JWT/安全/密码哈希/受信设备配置、
/// Token/MFA/风险分析等服务注册及对应选项校验器。
/// </summary>
public static class IdentityModuleExtensions
{
    /// <summary>
    /// 注册身份认证模块：JWT、安全密钥、密码哈希、受信设备选项绑定与校验，
    /// 以及 TokenService、AuthService、MFA、登录风险分析、地理位置等服务。
    /// </summary>
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<JwtSettings>(config.GetSection("JwtSettings"))
            .AddOptions<JwtSettings>()
            .Validate(s => !string.IsNullOrWhiteSpace(s.Issuer), "JwtSettings:Issuer 必填")
            .Validate(s => !string.IsNullOrWhiteSpace(s.Audience), "JwtSettings:Audience 必填")
            .Validate(s => s.AccessTokenExpirationMinutes > 0, "JwtSettings:AccessTokenExpirationMinutes 必须 > 0")
            .Validate(s => s.MaxActiveSessionsPerUser > 0 && s.MaxActiveSessionsPerUser <= 1000,
                "JwtSettings:MaxActiveSessionsPerUser 必须在 1..1000")
            .Validate(s => s.SessionChurnCleanupBatchSize > 0 && s.SessionChurnCleanupBatchSize <= 1000,
                "JwtSettings:SessionChurnCleanupBatchSize 必须在 1..1000")
            .ValidateOnStart();

        services.AddOptions<SecurityOptions>()
            .Bind(config.GetSection(SecurityOptions.SectionName))
            .Validate<IHostEnvironment>(
                (options, environment) =>
                    environment.IsDevelopment()
                    || environment.IsEnvironment("Testing")
                    || !string.IsNullOrWhiteSpace(options.SecretEncryptionKey),
                "生产环境必须配置 Security:SecretEncryptionKey")
            .Validate(s => s.KeyVersion > 0, "Security:KeyVersion 必须 > 0")
            .ValidateOnStart();

        services.AddOptions<PasswordHashingOptions>()
            .Bind(config.GetSection(PasswordHashingOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<TrustedDeviceOptions>()
            .Bind(config.GetSection(TrustedDeviceOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IDeviceInfo, DeviceInfoService>();
        services.AddSingleton<AccessTokenL1InvalidationBus>();
        services.AddSingleton<IAccessTokenL1InvalidationBus>(sp =>
            sp.GetRequiredService<AccessTokenL1InvalidationBus>());
        services.AddHostedService(sp => sp.GetRequiredService<AccessTokenL1InvalidationBus>());
        services.AddSingleton<IAuthCpuLimiter, AuthCpuLimiter>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();

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
        services.AddScoped<ISecurityEventStore, SecurityEventStore>();
        services.AddScoped<ITrustedDeviceService, TrustedDeviceService>();
        services.AddSingleton<LoginRiskAnalyzer>();
        services.AddSingleton<ILoginRiskAnalyzer>(sp => sp.GetRequiredService<LoginRiskAnalyzer>());
        services.AddHostedService(sp => sp.GetRequiredService<LoginRiskAnalyzer>());

        // 使用命名 HttpClient，IHttpClientFactory 管理连接池，避免套接字耗尽
        services.AddHttpClient(nameof(GeoLocationService), client =>
        {
            // GeoIP 请求包含公网 IP，生产环境必须走 TLS；后续可替换为本地 GeoIP 数据库。
            var baseUrl = config["GeoLocation:BaseUrl"] ?? "https://ip-api.com/";
            client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });
        services.AddSingleton<IGeoLocationService, GeoLocationService>();

        services.AddSingleton<IValidateOptions<PasswordHashingOptions>, PasswordHashingOptionsValidator>();
        services.AddSingleton<IValidateOptions<TrustedDeviceOptions>, TrustedDeviceOptionsValidator>();

        return services;
    }
}
