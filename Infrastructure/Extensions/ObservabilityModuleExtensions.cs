using Core.Settings;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Extensions;

/// <summary>
/// 可观测性模块的组合根：运行时健康指标托管服务与全局后台 Worker 并发管理器。
/// </summary>
public static class ObservabilityModuleExtensions
{
    /// <summary>
    /// 注册运行时健康指标、Worker 并发预算选项与 <see cref="WorkerConcurrencyManager"/>。
    /// </summary>
    public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration config)
    {
        services.AddHostedService<RuntimeHealthMetrics>();
        services.AddOptions<WorkerConcurrencyOptions>()
            .Bind(config.GetSection(WorkerConcurrencyOptions.SectionName))
            .Validate(o => o.GlobalMaxConcurrency >= 1, "WorkerConcurrency:GlobalMaxConcurrency 必须 >= 1")
            .Validate(o => o.NotificationDispatch >= 1, "WorkerConcurrency:NotificationDispatch 必须 >= 1")
            .Validate(o => o.EmailDispatch >= 1, "WorkerConcurrency:EmailDispatch 必须 >= 1")
            .Validate(o => o.AttachmentScan >= 1, "WorkerConcurrency:AttachmentScan 必须 >= 1")
            .Validate(o => o.DataExport >= 1, "WorkerConcurrency:DataExport 必须 >= 1")
            .ValidateOnStart();
        services.AddSingleton<WorkerConcurrencyManager>();
        return services;
    }
}
