using System.Net;
using System.Threading.RateLimiting;
using ChatApp.Realtime.Integration.DependencyInjection;
using ChatApp.Server.Middlewares;
using ChatApp.Server.RateLimiting;
using Core.Interfaces.Cache;
using Core.Settings;
using Infrastructure.Auth;
using Infrastructure.Data.Configurations;
using Infrastructure.Extensions;
using Infrastructure.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
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
        service.Configure<ProfileOptions>(config.GetSection(ProfileOptions.SectionName));
        service.Configure<NotificationOutboxOptions>(config.GetSection(NotificationOutboxOptions.SectionName));
        service.Configure<PasswordHashingOptions>(config.GetSection(PasswordHashingOptions.SectionName));
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

        service.Configure<SecurityOptions>(config.GetSection(SecurityOptions.SectionName));

        service.Configure<RealtimeGatewayOptions>(config.GetSection(RealtimeGatewayOptions.SectionName))
            .AddOptions<RealtimeGatewayOptions>()
            .Validate(s => !string.IsNullOrWhiteSpace(s.Host), "RealtimeGateway:Host 必填")
            .Validate(s => s.Port > 0, "RealtimeGateway:Port 必须 > 0")
            .ValidateOnStart();

        service.Configure<EmailConfig>(emailSettings)
            .AddOptions<EmailConfig>()
            .Validate(s => string.IsNullOrWhiteSpace(s.Host) || s.Port > 0, "EmailSettings:Port 无效")
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
        });

        builder.Services.AddRateLimiter(options =>
        {
            var rate = config.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>()
                       ?? new RateLimitingOptions();

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (ctx, token) =>
            {
                ctx.HttpContext.Response.ContentType = "application/json";
                await ctx.HttpContext.Response.WriteAsJsonAsync(new
                {
                    error = 429,
                    message = "请求过于频繁，请稍后再试"
                }, token);
            };

            options.AddPolicy("auth-login", httpContext =>
            {
                var cache = httpContext.RequestServices.GetRequiredService<ICacheProvider>();
                var key = GetClientKey(httpContext);
                return RateLimitPartition.Get(
                    $"auth-login:{key}",
                    partition => new RedisFixedWindowRateLimiter(
                        cache,
                        partition,
                        rate.AuthLoginPermitLimit,
                        TimeSpan.FromSeconds(Math.Max(1, rate.AuthLoginWindowSeconds))));
            });

            options.AddPolicy("auth-refresh", httpContext =>
            {
                var cache = httpContext.RequestServices.GetRequiredService<ICacheProvider>();
                var key = GetClientKey(httpContext);
                return RateLimitPartition.Get(
                    $"auth-refresh:{key}",
                    partition => new RedisFixedWindowRateLimiter(
                        cache,
                        partition,
                        rate.AuthRefreshPermitLimit,
                        TimeSpan.FromSeconds(Math.Max(1, rate.AuthRefreshWindowSeconds))));
            });

            options.AddPolicy("auth-email", httpContext =>
            {
                var cache = httpContext.RequestServices.GetRequiredService<ICacheProvider>();
                var key = GetClientKey(httpContext);
                return RateLimitPartition.Get(
                    $"auth-email:{key}",
                    partition => new RedisFixedWindowRateLimiter(
                        cache,
                        partition,
                        rate.AuthEmailPermitLimit,
                        TimeSpan.FromSeconds(Math.Max(1, rate.AuthEmailWindowSeconds))));
            });

            // 按登录用户限流邮箱变更，防止轮换目标邮箱刷信
            options.AddPolicy("user-email-change", httpContext =>
            {
                var cache = httpContext.RequestServices.GetRequiredService<ICacheProvider>();
                var userKey = httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                              ?? GetClientKey(httpContext);
                return RateLimitPartition.Get(
                    $"email-change:{userKey}",
                    partition => new RedisFixedWindowRateLimiter(
                        cache,
                        partition,
                        rate.UserEmailChangePermitLimit,
                        TimeSpan.FromSeconds(Math.Max(1, rate.UserEmailChangeWindowSeconds))));
            });

            options.AddPolicy("user-sensitive", httpContext =>
            {
                var cache = httpContext.RequestServices.GetRequiredService<ICacheProvider>();
                var userKey = httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                              ?? GetClientKey(httpContext);
                return RateLimitPartition.Get(
                    $"sensitive:{userKey}",
                    partition => new RedisFixedWindowRateLimiter(
                        cache,
                        partition,
                        rate.UserSensitivePermitLimit,
                        TimeSpan.FromSeconds(Math.Max(1, rate.UserSensitiveWindowSeconds))));
            });
        });

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
            options.Limits.MaxRequestBodySize = 3 * 1024 * 1024;
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
        app.UseMiddleware<RequestBodySizeLimitMiddleware>(3L * 1024 * 1024);
        app.UseCors("AllowSpecific");
        // 反代场景默认关闭：TLS 在 Nginx/LB 终止；需要时可设 EnableHttpsRedirection=true
        if (builder.Configuration.GetValue("EnableHttpsRedirection", false))
            app.UseHttpsRedirection();
        app.UseRequestTimeouts();
        app.UseRateLimiter();
        app.UseAuthentication();
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

    private static string GetClientKey(HttpContext httpContext)
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"{ip}:{httpContext.Request.Path}";
    }

    /// <summary>仅用于启动时校验连接串已配置（读取发生在宿主启动，晚于测试配置注入）。</summary>
    private sealed class ConnectionStringGuard
    {
        public string? DefaultConnection { get; set; }
        public string? Garnet { get; set; }
    }
}
