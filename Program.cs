using System.Threading.RateLimiting;
using ChatApp.Server.Middlewares;
using Core.Exceptions;
using Core.Settings;
using Infrastructure.Auth;
using Infrastructure.Data.Configurations;
using Infrastructure.Extensions;
using Infrastructure.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using NLog.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ChatApp.Server;

public abstract class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddNLog();

        builder.Services.AddControllers().AddJsonOptions(op =>
            AppJsonOptions.ApplyTo(op.JsonSerializerOptions));

        builder.Services.AddHttpContextAccessor();
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // 生产环境请显式配置 KnownProxies / KnownNetworks，避免伪造客户端 IP。
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });

        var config = builder.Configuration;
        var jwtSettings = config.GetSection("JwtSettings");
        var emailSettings = config.GetSection("EmailSettings");

        var service = await builder.Services.AddRedisCacheServices(config).ConfigureAwait(false);

        service.AddUserDbContext(config)
            .AddCoreServiceCollection();

        service.Configure<JwtSettings>(jwtSettings).AddOptions<JwtSettings>();
        service.Configure<RealtimeGatewayOptions>(config.GetSection(RealtimeGatewayOptions.SectionName));
        service.Configure<EmailConfig>(emailSettings).AddOptions<EmailConfig>();

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

        builder.Services.AddRateLimiter(options =>
        {
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
                RateLimitPartition.GetFixedWindowLimiter(
                    GetClientKey(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            options.AddPolicy("auth-refresh", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    GetClientKey(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            options.AddPolicy("auth-email", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    GetClientKey(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });

        var garnet = config.GetConnectionString("Garnet") ?? "127.0.0.1:6379";
        var db = config.GetConnectionString("DefaultConnection");

        var healthChecks = builder.Services.AddHealthChecks()
            .AddRedis(garnet, name: "garnet", tags: ["ready"]);
        if (!string.IsNullOrWhiteSpace(db))
            healthChecks.AddNpgSql(db, name: "postgres", tags: ["ready"]);

        // OTLP 端点可通过 OTEL_EXPORTER_OTLP_ENDPOINT 配置；未配置时导出器会尽力连接默认地址。
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("ChatApp.Server"))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter());

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 16 * 1024; // 认证与好友 API 足够；限制请求体膨胀
        });

        var app = builder.Build();

        app.UseForwardedHeaders();
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseCors("AllowSpecific");
        app.UseHttpsRedirection();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = _ => false,
        });

        app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
        });

        app.MapControllers();

        await app.RunAsync().ConfigureAwait(false);
    }

    private static string GetClientKey(HttpContext httpContext)
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"{ip}:{httpContext.Request.Path}";
    }
}
