using System.Net;
using Core.Interfaces;
using ChatApp.Realtime.Integration.DependencyInjection;
using ChatApp.Server.Middlewares;
using ChatApp.Server.RateLimiting;

using Core.Settings;
using Infrastructure.Auth;
using Infrastructure.Data.Configurations;
using Infrastructure.Extensions;
using Infrastructure.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Infrastructure.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NLog.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ChatApp.Server;

public abstract partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddNLog();

        builder.Services.AddControllers().AddJsonOptions(op =>
            AppJsonOptions.ApplyTo(op.JsonSerializerOptions));

        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "ChatApp.Server API";
                document.Info.Version = "v1";
                document.Info.Description = "当前路由前缀 api/* 即 v1；后续可用 api/v{version} 演进。";
                return Task.CompletedTask;
            });
        });
        builder.Services.AddProblemDetails();
        builder.Services.AddHttpContextAccessor();
        ConfigureForwardedHeaders(builder);

        var config = builder.Configuration;
        var jwtSettings = config.GetSection("JwtSettings");
        var emailSettings = config.GetSection("EmailSettings");

        var service = builder.Services.AddRedisCacheServices(config);

        service.AddUserDbContext(config)
            .AddCoreServiceCollection();

        service.Configure<AvatarStorageOptions>(config.GetSection(AvatarStorageOptions.SectionName));
        service.Configure<AttachmentStorageOptions>(config.GetSection(AttachmentStorageOptions.SectionName));
        service.Configure<ProfileOptions>(config.GetSection(ProfileOptions.SectionName));
        service.Configure<NotificationOutboxOptions>(config.GetSection(NotificationOutboxOptions.SectionName));
        service.Configure<PasswordHashingOptions>(config.GetSection(PasswordHashingOptions.SectionName));
        service.Configure<TrustedDeviceOptions>(config.GetSection(TrustedDeviceOptions.SectionName));
        service.Configure<MessageEvidenceOptions>(config.GetSection(MessageEvidenceOptions.SectionName));
        service.Configure<DataExportStorageOptions>(config.GetSection(DataExportStorageOptions.SectionName));
        service.Configure<AccountCleanupSagaOptions>(config.GetSection(AccountCleanupSagaOptions.SectionName));

        TryAddRealtimeIntegration(service, config);

        service.Configure<JwtSettings>(jwtSettings)
            .AddOptions<JwtSettings>()
            .Validate(s => !string.IsNullOrWhiteSpace(s.Issuer), "JwtSettings:Issuer 必填")
            .Validate(s => !string.IsNullOrWhiteSpace(s.Audience), "JwtSettings:Audience 必填")
            .Validate(s => s.AccessTokenExpirationMinutes > 0, "JwtSettings:AccessTokenExpirationMinutes 必须 > 0")
            .ValidateOnStart();

        service.AddOptions<SecurityOptions>()
            .Bind(config.GetSection(SecurityOptions.SectionName))
            .Validate<IHostEnvironment>(
                (options, environment) =>
                    environment.IsDevelopment()
                    || environment.IsEnvironment("Testing")
                    || !string.IsNullOrWhiteSpace(options.SecretEncryptionKey),
                "生产环境必须配置 Security:SecretEncryptionKey")
            .Validate(s => s.KeyVersion > 0, "Security:KeyVersion 必须 > 0")
            .ValidateOnStart();

        service.Configure<RealtimeGatewayOptions>(config.GetSection(RealtimeGatewayOptions.SectionName))
            .AddOptions<RealtimeGatewayOptions>()
            .Validate(s => !string.IsNullOrWhiteSpace(s.Host), "RealtimeGateway:Host 必填")
            .Validate(s => s.Port > 0, "RealtimeGateway:Port 必须 > 0")
            .ValidateOnStart();

        service.Configure<EmailConfig>(emailSettings)
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

        service.AddOptions<ForwardedHeadersSettings>()
            .Bind(config.GetSection(ForwardedHeadersSettings.SectionName))
            .Validate(s => s.KnownProxies.Length > 0 || s.KnownNetworks.Length > 0,
                "ForwardedHeaders 必须配置 KnownProxies 或 KnownNetworks")
            .ValidateOnStart();

        // 连接串校验推迟到宿主启动（ValidateOnStart），确保 Testing 下 WebApplicationFactory
        // 的 ConfigureAppConfiguration 覆盖已生效。
        service.AddOptions<ConnectionStringGuard>()
            .Configure<IConfiguration>((guard, configuration) =>
            {
                guard.DefaultConnection = configuration.GetConnectionString("DefaultConnection");
                guard.Garnet = configuration.GetConnectionString("Garnet");
            })
            .Validate(g => !string.IsNullOrWhiteSpace(g.DefaultConnection), "缺少 ConnectionStrings:DefaultConnection")
            .Validate(g => !string.IsNullOrWhiteSpace(g.Garnet), "缺少 ConnectionStrings:Garnet")
            .ValidateOnStart();

        service.AddAuthentication("Bearer")
            .AddScheme<AuthenticationSchemeOptions, OpaqueTokenAuthHandler>("Bearer", _ => { });

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowSpecific", policy =>
            {
                policy.WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .AllowCredentials();
            });
        });

        builder.Services.AddRequestTimeouts(options =>
        {
            options.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            options.AddPolicy("auth", TimeSpan.FromSeconds(20));
            options.AddPolicy("email", TimeSpan.FromSeconds(10));
            // 大附件上传需要更长超时；端点级标注，不影响普通 API。
            options.AddPolicy("attachment-upload", TimeSpan.FromMinutes(2));
        });

        // P0-6：单例分布式限流器 + 策略提供者（不再为每个分区键创建本地 RateLimiter 对象）。
        builder.Services.Configure<RateLimitingOptions>(config.GetSection(RateLimitingOptions.SectionName));
        builder.Services.AddSingleton<IDistributedRateLimiter, RedisDistributedRateLimiter>();
        builder.Services.AddSingleton<IRateLimitPolicyProvider, RateLimitPolicyProvider>();

        var healthChecks = builder.Services.AddHealthChecks()
            .AddRedis(
                sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("Garnet")
                      ?? throw new InvalidOperationException("缺少 ConnectionStrings:Garnet"),
                name: "garnet",
                tags: ["ready"])
            .AddNpgSql(
                sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")
                      ?? throw new InvalidOperationException("缺少 ConnectionStrings:DefaultConnection"),
                name: "postgres",
                tags: ["ready"]);

        ConfigureOpenTelemetry(builder);

        builder.WebHost.ConfigureKestrel(options =>
        {
            // 宿主安全上限：允许最大附件端点（30MB）+ 编码/表单开销。
            // 真实限制由端点元数据 [RequestSizeLimit] 决定，不再在此收紧。
            options.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });

        var app = builder.Build();

        if (args.Any(a => string.Equals(a, "--migrate", StringComparison.OrdinalIgnoreCase)))
        {
            await using var scope = app.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<Infrastructure.Data.UserDbContext>();
            await dbContext.Database.MigrateAsync().ConfigureAwait(false);
            Console.WriteLine("Database migrations applied.");
            return;
        }

        app.UseForwardedHeaders();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        // TestServer 不完全遵循 Kestrel MaxRequestBodySize，用 Content-Length 显式拒绝超限请求
        app.UseRouting();
        app.UseMiddleware<RequestBodySizeLimitMiddleware>(32L * 1024 * 1024);
        app.UseCors("AllowSpecific");
        // 反代场景默认关闭：TLS 在 Nginx/LB 终止；需要时可设 EnableHttpsRedirection=true
        if (builder.Configuration.GetValue("EnableHttpsRedirection", false))
            app.UseHttpsRedirection();
        app.UseRequestTimeouts();
        // P0-2：先认证再限流，使 user-email-change / user-sensitive 能按用户 Claim 分区。
        // 匿名接口（login/register）仍按 IP/device/email 多维限流。
        app.UseAuthentication();
        app.UseDistributedRateLimiting();
        app.UseAuthorization();

        var avatarRoot = builder.Configuration[$"{AvatarStorageOptions.SectionName}:LocalRootPath"]
                         ?? "App_Data/avatars";
        var avatarPublic = builder.Configuration[$"{AvatarStorageOptions.SectionName}:PublicBaseUrl"]
                           ?? "/static/avatars";
        Directory.CreateDirectory(avatarRoot);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
                Path.GetFullPath(avatarRoot)),
            RequestPath = avatarPublic.StartsWith('/') ? avatarPublic : "/" + avatarPublic,
        });

        // 私有附件禁止匿名静态挂载；须走 GET /api/attachments/{id}/download（会话成员鉴权）。
        // 非 Development 强制关闭 UsePublicStatic，避免配置误开导致长期匿名访问。
        var usePublicAttachments = builder.Configuration.GetValue(
            $"{AttachmentStorageOptions.SectionName}:UsePublicStatic", false);
        if (usePublicAttachments && !app.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "非开发环境禁止 AttachmentStorage:UsePublicStatic=true；私有附件必须经鉴权下载接口。");
        }

        var attachmentRoot = builder.Configuration[$"{AttachmentStorageOptions.SectionName}:LocalRootPath"]
                             ?? "App_Data/attachments";
        Directory.CreateDirectory(attachmentRoot);
        if (usePublicAttachments)
        {
            var attachmentPublic = builder.Configuration[$"{AttachmentStorageOptions.SectionName}:PublicBaseUrl"]
                                   ?? "/static/attachments";
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
                    Path.GetFullPath(attachmentRoot)),
                RequestPath = attachmentPublic.StartsWith('/') ? attachmentPublic : "/" + attachmentPublic,
            });
        }

        if (app.Environment.IsDevelopment())
            app.MapOpenApi();

        app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = _ => false,
        });

        app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
        });

        app.MapControllers();

        if (app.Environment.IsEnvironment("Testing"))
        {
            // 仅测试环境：用于验证 ExceptionHandlingMiddleware → ProblemDetails。
            app.MapGet("/api/__test/problem", () =>
            {
                throw new ArgumentException("intentional test probe");
            });
        }

        await app.RunAsync().ConfigureAwait(false);
    }

    private static void ConfigureForwardedHeaders(WebApplicationBuilder builder)
    {
        var settings = builder.Configuration
            .GetSection(ForwardedHeadersSettings.SectionName)
            .Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (var proxy in settings.KnownProxies)
            {
                if (IPAddress.TryParse(proxy, out var ip))
                    options.KnownProxies.Add(ip);
            }

            foreach (var cidr in settings.KnownNetworks)
            {
                if (TryParseCidr(cidr, out var network))
                    options.KnownIPNetworks.Add(network);
            }

            // 本地开发：未配置任何可信代理时，仅信任回环，避免任意来源 XFF 生效。
            if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 0)
            {
                options.KnownProxies.Add(IPAddress.Loopback);
                options.KnownProxies.Add(IPAddress.IPv6Loopback);
            }
        });
    }

    private static bool TryParseCidr(string cidr, out System.Net.IPNetwork network)
    {
        network = default!;
        try
        {
            network = System.Net.IPNetwork.Parse(cidr);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ConfigureOpenTelemetry(WebApplicationBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
                           ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("ChatApp.Server"))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource("Infrastructure.Caching.Redis");
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    t.AddOtlpExporter();
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Infrastructure.Caching")
                    .AddMeter("Infrastructure.Email.Outbox")
                    .AddMeter("Infrastructure.Notification.Outbox")
                    .AddMeter("Infrastructure.Avatar.Reencode")
                    .AddMeter("Infrastructure.Moderation.Evidence")
                    .AddMeter("Infrastructure.Auth")
                    .AddMeter("Infrastructure.PasswordHashing")
                    .AddMeter("Infrastructure.TrustedDevice")
                    .AddMeter("Infrastructure.DataExport")
                    .AddMeter("Infrastructure.LoginRisk")
                    .AddMeter("Infrastructure.Runtime");
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    m.AddOtlpExporter();
            });
    }

    private static void TryAddRealtimeIntegration(IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection(RealtimeIntegrationHostOptions.SectionName);
        var hostOpts = section.Get<RealtimeIntegrationHostOptions>() ?? new RealtimeIntegrationHostOptions();
        if (string.IsNullOrWhiteSpace(hostOpts.Url))
            return;

        services.AddChatAppRealtimeIntegration(new ChatApp.Realtime.Integration.Configuration.RealtimeIntegrationOptions
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

    /// <summary>仅用于启动时校验连接串已配置（读取发生在宿主启动，晚于测试配置注入）。</summary>
    private sealed class ConnectionStringGuard
    {
        public string? DefaultConnection { get; set; }
        public string? Garnet { get; set; }
    }
}
