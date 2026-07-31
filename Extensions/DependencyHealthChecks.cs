using Core.Interfaces;
using Core.Settings;
using Infrastructure.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ChatApp.Realtime.Integration;

namespace ChatApp.Server.Extensions;

internal sealed class AttachmentStorageHealthCheck(
    IOptions<AttachmentStorageOptions> options,
    IAttachmentMetadataStore metadata,
    IAttachmentStorage storage,
    IHostEnvironment environment) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        if (string.Equals(opts.Provider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(opts.S3Bucket))
                return HealthCheckResult.Unhealthy("S3 bucket 未配置");
        }
        else if (string.IsNullOrWhiteSpace(opts.LocalRootPath))
        {
            return HealthCheckResult.Unhealthy("本地附件目录未配置");
        }

        if (storage is IObjectStoreHealthProbe probe)
        {
            try
            {
                await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new HealthCheckResult(
                    HealthStatus.Unhealthy,
                    "附件对象存储不可用",
                    ex,
                    new Dictionary<string, object>
                    {
                        ["provider"] = opts.Provider,
                    });
            }
        }

        if (!metadata.IsAvailable)
        {
            return new HealthCheckResult(
                HealthStatus.Degraded,
                "附件元数据服务不可用",
                data: new Dictionary<string, object>
                {
                    ["reason"] = metadata.UnavailableReason,
                });
        }

        if (string.Equals(opts.ScannerProvider, "DenyList", StringComparison.OrdinalIgnoreCase)
            && !environment.IsDevelopment()
            && !environment.IsEnvironment("Testing"))
        {
            return new HealthCheckResult(
                HealthStatus.Degraded,
                "附件当前仅启用策略过滤，未接入恶意软件扫描引擎",
                data: new Dictionary<string, object>
                {
                    ["scanner"] = opts.ScannerProvider,
                    ["security_boundary"] = "policy_only",
                });
        }

        return HealthCheckResult.Healthy("附件存储配置就绪");
    }
}

internal sealed class MessageEvidenceHealthCheck(
    IOptions<MessageEvidenceOptions> options,
    IServiceProvider services) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var hasRealtime = !string.IsNullOrWhiteSpace(opts.RealtimeConnectionString)
                          || services.GetService<IRealtimeMessageBus>() is not null;
        return Task.FromResult(hasRealtime
            ? HealthCheckResult.Healthy("消息证据服务已配置")
            : new HealthCheckResult(
                HealthStatus.Degraded,
                "消息证据服务不可用",
                data: new Dictionary<string, object>
                {
                    ["reason"] = "未配置 Realtime Postgres 或 NATS 总线",
                }));
    }
}

internal sealed class RealtimeOutboxHealthCheck(IServiceProvider services) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(services.GetService<IRealtimeMessageBus>() is not null
            ? HealthCheckResult.Healthy("Realtime outbox 已连接")
            : new HealthCheckResult(
                HealthStatus.Degraded,
                "Realtime outbox 不可用",
                data: new Dictionary<string, object>
                {
                    ["reason"] = "未注册 Realtime NATS 总线",
                }));
}

internal sealed class DataExportHealthCheck(
    IOptions<DataExportStorageOptions> options,
    IDataExportBlobStore blobStore,
    IHostEnvironment environment) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        if (string.Equals(opts.Provider, "S3", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(opts.S3Bucket))
            return HealthCheckResult.Unhealthy("导出 S3 bucket 未配置");

        if (string.Equals(opts.Provider, "Local", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(opts.LocalRootPath))
            return HealthCheckResult.Unhealthy("导出本地目录未配置");

        if (blobStore is IObjectStoreHealthProbe probe)
        {
            try
            {
                await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new HealthCheckResult(
                    HealthStatus.Unhealthy,
                    "导出对象存储不可用",
                    ex,
                    new Dictionary<string, object>
                    {
                        ["provider"] = opts.Provider,
                    });
            }
        }

        if (string.Equals(opts.Provider, "Local", StringComparison.OrdinalIgnoreCase)
            && !environment.IsDevelopment()
            && !environment.IsEnvironment("Testing"))
        {
            return new HealthCheckResult(
                HealthStatus.Degraded,
                "导出当前使用单机本地存储，多实例和重启恢复能力不可用",
                data: new Dictionary<string, object>
                {
                    ["provider"] = opts.Provider,
                    ["capability"] = "single_instance_only",
                });
        }

        return HealthCheckResult.Healthy("导出存储配置就绪");
    }
}

internal static class HealthResponseWriter
{
    public static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var entries = report.Entries.ToDictionary(
            pair => pair.Key,
            pair => new
            {
                status = pair.Value.Status.ToString().ToLowerInvariant(),
                description = pair.Value.Description,
                durationMs = Math.Round(pair.Value.Duration.TotalMilliseconds, 2),
                data = pair.Value.Data,
            });

        await context.Response.WriteAsJsonAsync(
            new
            {
                status = report.Status.ToString().ToLowerInvariant(),
                totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
                entries,
            }).ConfigureAwait(false);
    }
}
