using Core.Interfaces;
using Core.Settings;
using Infrastructure.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ChatApp.Realtime.Integration;
using Npgsql;

namespace ChatApp.Server.Extensions;

internal sealed class AttachmentStorageHealthCheck(
    IOptions<AttachmentStorageOptions> options,
    IAttachmentMetadataStore metadata,
    IAttachmentStorage storage,
    IAvatarStorage avatarStorage,
    IAttachmentContentScanner scanner,
    IOptions<HealthDependencyOptions> dependencyOptions,
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

        if (storage is IS3LifecycleHealthProbe attachmentLifecycle)
        {
            try
            {
                await attachmentLifecycle.ValidateLifecycleAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new HealthCheckResult(
                    HealthStatus.Unhealthy,
                    "附件 S3 Lifecycle 配置不可用",
                    ex,
                    new Dictionary<string, object>
                    {
                        ["provider"] = opts.Provider,
                        ["capability"] = "candidate_lifecycle",
                    });
            }
        }

        if (avatarStorage is IS3LifecycleHealthProbe avatarLifecycle)
        {
            try
            {
                await avatarLifecycle.ValidateLifecycleAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new HealthCheckResult(
                    HealthStatus.Unhealthy,
                    "头像 S3 Lifecycle 配置不可用",
                    ex,
                    new Dictionary<string, object>
                    {
                        ["capability"] = "avatar_candidate_lifecycle",
                    });
            }
        }

        if (!metadata.IsAvailable)
        {
            return new HealthCheckResult(
                dependencyOptions.Value.RequireAttachmentMetadata(metadata.IsAvailable)
                    ? HealthStatus.Unhealthy
                    : HealthStatus.Degraded,
                "附件元数据服务不可用",
                data: new Dictionary<string, object>
                {
                    ["reason"] = metadata.UnavailableReason,
                });
        }

        if (metadata is IAttachmentMetadataHealthProbe metadataProbe)
        {
            try
            {
                await metadataProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return new HealthCheckResult(
                    dependencyOptions.Value.RequireAttachmentMetadata(configured: true)
                        ? HealthStatus.Unhealthy
                        : HealthStatus.Degraded,
                    "附件元数据 Realtime PostgreSQL 不可用",
                    ex,
                    new Dictionary<string, object> { ["provider"] = "realtime-postgres" });
            }
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

        if (dependencyOptions.Value.IsWorker
            && string.Equals(opts.ScannerProvider, "ClamAV", StringComparison.OrdinalIgnoreCase)
            && scanner is IAttachmentScannerHealthProbe scannerProbe)
        {
            try
            {
                await scannerProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return new HealthCheckResult(
                    HealthStatus.Unhealthy,
                    "ClamAV 扫描服务不可用",
                    ex,
                    new Dictionary<string, object> { ["provider"] = "clamav" });
            }
        }

        return HealthCheckResult.Healthy("附件存储配置就绪");
    }
}

internal sealed class MessageEvidenceHealthCheck(
    IOptions<MessageEvidenceOptions> options,
    RealtimePostgresDataSource dataSource,
    IServiceProvider services,
    IOptions<HealthDependencyOptions> dependencyOptions) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        try
        {
            if (dataSource.DataSource is { } postgres)
            {
                await using var connection = await postgres.OpenConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return HealthCheckResult.Healthy("消息证据 Realtime PostgreSQL 可用");
            }

            if (services.GetService<IRealtimeMessageBus>() is { } bus)
            {
                await bus.PingAsync(cancellationToken).ConfigureAwait(false);
                return HealthCheckResult.Healthy("消息证据 Realtime 总线可用");
            }

            return new HealthCheckResult(
                dependencyOptions.Value.RequireMessageEvidence(configured: false)
                    ? HealthStatus.Unhealthy
                    : HealthStatus.Degraded,
                "消息证据服务不可用",
                data: new Dictionary<string, object>
                {
                    ["reason"] = "未配置 Realtime Postgres 或 NATS 总线",
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(
                HealthStatus.Unhealthy,
                "消息证据实时探测失败",
                ex,
                new Dictionary<string, object> { ["provider"] = "realtime" });
        }
    }
}

internal sealed class RealtimeOutboxHealthCheck(
    IServiceProvider services,
    IOptions<HealthDependencyOptions> dependencyOptions) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (services.GetService<IRealtimeMessageBus>() is not { } bus)
            return new HealthCheckResult(
                dependencyOptions.Value.RequireRealtimeOutbox
                    ? HealthStatus.Unhealthy
                    : HealthStatus.Degraded,
                "Realtime outbox 不可用",
                data: new Dictionary<string, object>
                {
                    ["reason"] = "未注册 Realtime NATS 总线",
                });

        try
        {
            await bus.PingAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("Realtime outbox 总线可用");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(HealthStatus.Unhealthy, "Realtime outbox 实时探测失败", ex);
        }
    }
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

        if (blobStore is IS3LifecycleHealthProbe lifecycle)
        {
            try
            {
                await lifecycle.ValidateLifecycleAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new HealthCheckResult(
                    HealthStatus.Unhealthy,
                    "导出 S3 Lifecycle 配置不可用",
                    ex,
                    new Dictionary<string, object>
                    {
                        ["provider"] = opts.Provider,
                        ["capability"] = "candidate_lifecycle",
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
    public static async Task WriteSimpleAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString().ToLowerInvariant(),
        }).ConfigureAwait(false);
    }

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
