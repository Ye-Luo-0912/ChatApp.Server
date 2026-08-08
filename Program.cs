using System.Net;
using ChatApp.Server.Extensions;
using ChatApp.Server.Middlewares;
using ChatApp.Server.RateLimiting;

using Core.Settings;
using Infrastructure.Diagnostics;
using Infrastructure.Extensions;
using Infrastructure.Serialization;
using ChatApp.Contracts.Http;
using Infrastructure.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NLog.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;

namespace ChatApp.Server;

public abstract partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddNLog();

        var config = builder.Configuration;
        // Migrations must not inherit the API's 3–5 second request budget.
        // Set the bound option before DbContext registration so the design
        // and runtime migration paths share the same explicit timeout.
        if (args.Any(a => string.Equals(a, "--migrate", StringComparison.OrdinalIgnoreCase)))
            config[$"{DatabasePoolOptions.SectionName}:UseMigrationTimeout"] = "true";
        var configuredRole = builder.WebHost.GetSetting($"{DatabasePoolOptions.SectionName}:Role");
        var processRole = DatabasePoolOptions.ParseRole(
            configuredRole ?? config[$"{DatabasePoolOptions.SectionName}:Role"]);
        var runsApi = processRole is DatabaseProcessRole.Api or DatabaseProcessRole.All;
        var runsWorkers = processRole is DatabaseProcessRole.Worker or DatabaseProcessRole.All;

        if (runsApi)
        {
            builder.Services.AddControllers().AddJsonOptions(op =>
            {
                AppJsonOptions.ApplyTo(op.JsonSerializerOptions);
                op.JsonSerializerOptions.TypeInfoResolverChain.Insert(
                    0,
                    HttpContractsJsonSerializerContext.Default);
            });

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
            ConfigureForwardedHeaders(builder);
        }

        builder.Services.AddHttpContextAccessor();

        var service = builder.Services.AddRedisCacheServices(config);

        service.AddUserDbContext(config);

        // 组合根：按模块拆分的服务注册与配置绑定。
        // 各模块位于 Infrastructure.Extensions（业务/基础设施层）
        // 与 ChatApp.Server.Extensions（API 层，引用宿主独占类型）。
        service.AddIdentityModule(
            config,
            registerApiLocalHostedServices: runsApi,
            registerWorkerHostedServices: runsWorkers);
        service.AddFriendshipModule();
        service.AddAttachmentModule(config, registerWorkerHostedServices: runsWorkers);
        service.AddNotificationModule(config, registerWorkerHostedServices: runsWorkers);
        service.AddModerationModule(config, registerWorkerHostedServices: runsWorkers);
        service.AddAccountLifecycleModule(config, registerWorkerHostedServices: runsWorkers);
        service.AddRealtimeIntegrationModule(config, registerWorkerHostedServices: runsWorkers);
        service.AddObservability(config, registerWorkerHostedServices: runsWorkers);
        if (runsApi)
            service.AddApiPolicies(config);
        else
            service.AddWorkerHealthChecks(config);

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

        var avatarRoot = config[$"{AvatarStorageOptions.SectionName}:LocalRootPath"] ?? "App_Data/avatars";
        var attachmentRoot = config[$"{AttachmentStorageOptions.SectionName}:LocalRootPath"] ?? "App_Data/attachments";
        var usePublicAttachments = config.GetValue($"{AttachmentStorageOptions.SectionName}:UsePublicStatic", false);

        if (usePublicAttachments && !app.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "非开发环境禁止 AttachmentStorage:UsePublicStatic=true；私有附件必须经鉴权下载接口。");
        }

        Directory.CreateDirectory(avatarRoot);
        Directory.CreateDirectory(attachmentRoot);

        app.UseRouting();
        if (runsApi)
        {
        if (app.Environment.IsEnvironment("Performance") || app.Environment.IsEnvironment("Testing"))
        {
            app.Use(async (context, next) =>
            {
                var dbCounter = context.RequestServices
                    .GetRequiredService<DbCommandCounterInterceptor>();
                dbCounter.BeginRequest(context);
                context.Response.OnStarting(() =>
                {
                context.Response.Headers["X-ChatApp-Db-Commands"] =
                    dbCounter.GetRequestCount(context).ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers["X-ChatApp-Auth-Db-Commands"] =
                    dbCounter.GetAuthFenceRequestCount(context).ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers["X-ChatApp-Db-Pool-Wait-Ms"] =
                    dbCounter.GetPoolWaitMilliseconds(context).ToString(
                        "F3",
                        System.Globalization.CultureInfo.InvariantCulture);
                return Task.CompletedTask;
                });
                await next(context).ConfigureAwait(false);
            });
        }
        app.UseForwardedHeaders();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        // TestServer 不完全遵循 Kestrel MaxRequestBodySize，用 Content-Length 显式拒绝超限请求
        app.UseMiddleware<RequestBodySizeLimitMiddleware>(32L * 1024 * 1024);
        app.UseCors("AllowSpecific");
        // 反代场景默认关闭：TLS 在 Nginx/LB 终止；需要时可设 EnableHttpsRedirection=true
        if (builder.Configuration.GetValue("EnableHttpsRedirection", false))
            app.UseHttpsRedirection();
        app.UseRequestTimeouts();
        // P0-2：先认证再限流，使 user-email-change / user-sensitive 能按用户 Claim 分区。
        // 匿名接口（login/register）仍按 IP/device/email 多维限流。
        app.UseAuthentication();
        app.UseMiddleware<ChatApp.Server.Authorization.DeletionPendingAccessMiddleware>();
        app.UseDistributedRateLimiting();
        app.UseAuthorization();

        var avatarPublic = builder.Configuration[$"{AvatarStorageOptions.SectionName}:PublicBaseUrl"]
                           ?? "/static/avatars";
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
                Path.GetFullPath(avatarRoot)),
            RequestPath = avatarPublic.StartsWith('/') ? avatarPublic : "/" + avatarPublic,
        });

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

        // 诊断端点：仅供压测/测试环境采样 allocations/Redis commands/DB queries。
        // 生产环境不映射——暴露 uptime/资源压力/后端活动趋势会构成信息泄露。
        if (app.Environment.IsEnvironment("Performance") || app.Environment.IsEnvironment("Testing"))
            MapDebugMetrics(app);

        app.MapControllers();

        if (app.Environment.IsEnvironment("Testing"))
        {
            // 仅测试环境：用于验证 ExceptionHandlingMiddleware → ProblemDetails。
            app.MapGet("/api/__test/problem", () =>
            {
                throw new ArgumentException("intentional test probe");
            });
        }

        }

        // Worker processes have no public API pipeline, but their internal
        // performance stage still needs process-local DB/Redis/GC counters.
        // This route exists only in Performance/Testing and is bound to the
        // worker's private probe port by the deployment/workflow.
        if (!runsApi
            && (app.Environment.IsEnvironment("Performance")
                || app.Environment.IsEnvironment("Testing")))
        {
            MapDebugMetrics(app);
        }

        MapHealthEndpoints(app, runsApi);

        await app.RunAsync().ConfigureAwait(false);
    }

    private static void MapHealthEndpoints(WebApplication app, bool runsApi)
    {
        app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = Extensions.HealthResponseWriter.WriteSimpleAsync,
        });

        app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResultStatusCodes =
            {
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = StatusCodes.Status200OK,
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status200OK,
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
            ResponseWriter = Extensions.HealthResponseWriter.WriteSimpleAsync,
        });

        // Worker processes intentionally have no HTTP authentication pipeline.
        // Keep their probe surface limited to the orchestrator-facing
        // liveness/readiness endpoints; detailed dependency diagnostics are
        // only mapped on an API process where the admin policy can protect it.
        if (!runsApi)
            return;

        var dependencies = app.MapHealthChecks("/health/dependencies", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("dependencies"),
            ResultStatusCodes =
            {
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = StatusCodes.Status200OK,
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status200OK,
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
            ResponseWriter = Extensions.HealthResponseWriter.WriteAsync,
        });

        var capabilities = app.MapHealthChecks("/health/capabilities", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("capabilities"),
            ResultStatusCodes =
            {
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = StatusCodes.Status200OK,
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status200OK,
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
            ResponseWriter = Extensions.HealthResponseWriter.WriteAsync,
        });

        if (runsApi)
        {
            var adminPolicy = ChatApp.Server.Authorization.AuthoritativeAdminAuthorization.PolicyName;
            dependencies.RequireAuthorization(adminPolicy);
            capabilities.RequireAuthorization(adminPolicy);
        }
    }

    private static void MapDebugMetrics(WebApplication app)
    {
        app.MapGet("/debug/metrics", (IConnectionMultiplexer redis, DbCommandCounterInterceptor dbCounter) =>
        {
            var authFence = AuthSecurityMetrics.GetAuthFenceSnapshot();
            return Results.Ok(new Dictionary<string, object>
            {
                ["allocated_bytes"] = GC.GetTotalAllocatedBytes(),
                ["gc_heap_bytes"] = GC.GetTotalMemory(false),
                ["gen0_collections"] = GC.CollectionCount(0),
                ["gen1_collections"] = GC.CollectionCount(1),
                ["gen2_collections"] = GC.CollectionCount(2),
                ["working_set_bytes"] = Environment.WorkingSet,
                ["redis_total_commands"] = redis.OperationCount,
                ["db_total_commands"] = dbCounter.TotalCommandsExecuted,
                ["db_auth_fence_commands_total"] = dbCounter.TotalAuthFenceCommands,
                ["db_pool_wait_ms_total"] = app.Services
                    .GetRequiredService<DbConnectionPoolWaitInterceptor>()
                    .TotalWaitMilliseconds,
                ["gc_pause_ms_total"] = GetGcPauseMilliseconds(),
                ["auth_fence_l1_hits_total"] = authFence.L1Hits,
                ["auth_fence_l1_misses_total"] = authFence.L1Misses,
                ["auth_fence_garnet_reads_total"] = authFence.GarnetReads,
                ["auth_fence_postgres_reads_total"] = authFence.PostgresReads,
                ["db_commands_by_endpoint"] = dbCounter.GetEndpointCounts(),
                ["uptime_ms"] = Environment.TickCount64,
            });
        });
    }

    private static double GetGcPauseMilliseconds()
    {
        var pauses = GC.GetGCMemoryInfo().PauseDurations;
        var total = 0.0;
        foreach (var pause in pauses)
            total += pause.TotalMilliseconds;
        return total;
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

    /// <summary>仅用于启动时校验连接串已配置（读取发生在宿主启动，晚于测试配置注入）。</summary>
    private sealed class ConnectionStringGuard
    {
        public string? DefaultConnection { get; set; }
        public string? Garnet { get; set; }
    }
}
