using System.Net;
using System.Threading.RateLimiting;
using ChatApp.Server.Middlewares;
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
        ConfigureForwardedHeaders(builder);

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

        ConfigureOpenTelemetry(builder);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 16 * 1024;
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });

        var app = builder.Build();

        app.UseForwardedHeaders();
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseCors("AllowSpecific");
        app.UseHttpsRedirection();
        app.UseRequestTimeouts();
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
                t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    t.AddOtlpExporter();
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    m.AddOtlpExporter();
            });
    }

    private static string GetClientKey(HttpContext httpContext)
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"{ip}:{httpContext.Request.Path}";
    }
}
