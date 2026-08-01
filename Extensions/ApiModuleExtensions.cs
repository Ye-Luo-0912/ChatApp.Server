using System;
using ChatApp.Server.RateLimiting;
using Core.Interfaces;
using Core.Settings;
using Infrastructure.Auth;
using Infrastructure.RateLimiting;
using Infrastructure.Extensions;
using Infrastructure.Validation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatApp.Server.Extensions;

/// <summary>
/// API 层策略模块的组合根：分布式限流、转发头校验、认证、CORS、请求超时与健康检查。
/// 该模块位于宿主项目，因其引用 <see cref="IRateLimitPolicyProvider"/> 等宿主层类型
/// 与宿主独占的健康检查 NuGet 包，不能下沉到 Infrastructure。
/// </summary>
public static class ApiModuleExtensions
{
    /// <summary>注册 API 层策略：限流、转发头、认证、CORS、超时与健康检查。</summary>
    public static IServiceCollection AddApiPolicies(this IServiceCollection services, IConfiguration config)
    {
        // P0-6：单例分布式限流器 + 策略提供者（不再为每个分区键创建本地 RateLimiter 对象）。
        services.AddValidatedOptions<RateLimitingOptions, RateLimitingOptionsValidator>(
            config, RateLimitingOptions.SectionName);
        services.AddSingleton<IDistributedRateLimiter, RedisDistributedRateLimiter>();
        services.AddSingleton<IRateLimitPolicyProvider, RateLimitPolicyProvider>();
        services.AddSingleton<RateLimitDimensionKeyHasher>();
        services.AddScoped<AccountRateLimitActionFilter>();

        services.AddOptions<ForwardedHeadersSettings>()
            .Bind(config.GetSection(ForwardedHeadersSettings.SectionName))
            .Validate(s => s.KnownProxies.Length > 0 || s.KnownNetworks.Length > 0,
                "ForwardedHeaders 必须配置 KnownProxies 或 KnownNetworks")
            .ValidateOnStart();

        services.AddAuthentication("Bearer")
            .AddScheme<AuthenticationSchemeOptions, OpaqueTokenAuthHandler>("Bearer", _ => { });

        services.AddCors(options =>
        {
            options.AddPolicy("AllowSpecific", policy =>
            {
                policy.WithOrigins(config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .AllowCredentials();
            });
        });

        services.AddRequestTimeouts(options =>
        {
            options.DefaultPolicy = new RequestTimeoutPolicy
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            options.AddPolicy("auth", TimeSpan.FromSeconds(20));
            options.AddPolicy("email", TimeSpan.FromSeconds(10));
            // 大附件上传需要更长超时；端点级标注，不影响普通 API。
            options.AddPolicy("attachment-upload", TimeSpan.FromMinutes(2));
        });

        AddApplicationHealthChecks(services);

        return services;
    }
    /// <summary>注册 Worker 的依赖健康检查；不注册认证、限流或 HTTP API 策略。</summary>
    public static IServiceCollection AddWorkerHealthChecks(this IServiceCollection services)
    {
        AddApplicationHealthChecks(services);
        return services;
    }

    private static void AddApplicationHealthChecks(IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddRedis(
                sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("Garnet")
                      ?? throw new InvalidOperationException("缺少 ConnectionStrings:Garnet"),
                name: "garnet",
                tags: ["ready", "identity", "dependencies", "capabilities"])
            .AddNpgSql(
                sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")
                      ?? throw new InvalidOperationException("缺少 ConnectionStrings:DefaultConnection"),
                name: "postgres",
                tags: ["ready", "identity", "dependencies", "capabilities"])
            .AddCheck<AttachmentStorageHealthCheck>(
                "attachments",
                tags: ["ready", "dependencies", "capabilities"])
            .AddCheck<MessageEvidenceHealthCheck>(
                "message-evidence",
                tags: ["ready", "dependencies", "capabilities"])
            .AddCheck<RealtimeOutboxHealthCheck>(
                "realtime-outbox",
                tags: ["ready", "dependencies", "capabilities"])
            .AddCheck<DataExportHealthCheck>(
                "data-export",
                tags: ["ready", "dependencies", "capabilities"]);
    }
}
