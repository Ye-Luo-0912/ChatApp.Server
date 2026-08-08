using Core.Settings;
using Core.Interfaces;
using Infrastructure.Diagnostics;
using Infrastructure.Services;
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
    public static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration config,
        bool registerWorkerHostedServices = false)
    {
        services.AddHostedService<RuntimeHealthMetrics>();
        services.AddOptions<RuntimeHealthMetricsOptions>()
            .Bind(config.GetSection(RuntimeHealthMetricsOptions.SectionName))
            .Validate(o => o.RedisPingIntervalSeconds >= 5,
                "RuntimeHealthMetrics:RedisPingIntervalSeconds 必须 >= 5")
            .ValidateOnStart();
        services.AddOptions<WorkerConcurrencyOptions>()
            .Bind(config.GetSection(WorkerConcurrencyOptions.SectionName))
            .Validate(o => o.GlobalMaxConcurrency >= 1, "WorkerConcurrency:GlobalMaxConcurrency 必须 >= 1")
            .Validate(o => o.NotificationDispatch >= 1, "WorkerConcurrency:NotificationDispatch 必须 >= 1")
            .Validate(o => o.EmailDispatch >= 1, "WorkerConcurrency:EmailDispatch 必须 >= 1")
            .Validate(o => o.AttachmentScan >= 1, "WorkerConcurrency:AttachmentScan 必须 >= 1")
            .Validate(o => o.AttachmentProjection >= 1, "WorkerConcurrency:AttachmentProjection 必须 >= 1")
            .Validate(o => o.AttachmentConfirm >= 1, "WorkerConcurrency:AttachmentConfirm 必须 >= 1")
            .Validate(o => o.AttachmentBlobDelete >= 1, "WorkerConcurrency:AttachmentBlobDelete 必须 >= 1")
            .Validate(o => o.AccountDeletion >= 1, "WorkerConcurrency:AccountDeletion 必须 >= 1")
            .Validate(o => o.ModerationRevocation >= 1, "WorkerConcurrency:ModerationRevocation 必须 >= 1")
            .Validate(o => o.DataExport >= 1, "WorkerConcurrency:DataExport 必须 >= 1")
            .Validate(o => o.SecurityAudit >= 1, "WorkerConcurrency:SecurityAudit 必须 >= 1")
            .Validate(o => o.SecurityRevocation >= 1, "WorkerConcurrency:SecurityRevocation 必须 >= 1")
            .Validate(o => o.LoginRiskAnalysis >= 1, "WorkerConcurrency:LoginRiskAnalysis 必须 >= 1")
            .ValidateOnStart();
        services.AddOptions<JobRetentionPolicy>()
            .Bind(config.GetSection(JobRetentionPolicy.SectionName))
            .Validate(o => o.PollIntervalSeconds >= 30, "JobRetention:PollIntervalSeconds 必须 >= 30")
            .Validate(o => o.BatchSize >= 1 && o.BatchSize <= 5000, "JobRetention:BatchSize 必须在 1..5000")
            .Validate(o => o.ScanJobRetentionDays >= 1, "JobRetention:ScanJobRetentionDays 必须 >= 1")
            .Validate(o => o.ScanProjectionRetentionDays >= 1, "JobRetention:ScanProjectionRetentionDays 必须 >= 1")
            .Validate(o => o.ScanAuditRetentionDays >= 1, "JobRetention:ScanAuditRetentionDays 必须 >= 1")
            .Validate(o => o.AttachmentConfirmSagaRetentionDays >= 1, "JobRetention:AttachmentConfirmSagaRetentionDays 必须 >= 1")
            .Validate(o => o.AttachmentBlobDeleteRetentionDays >= 1, "JobRetention:AttachmentBlobDeleteRetentionDays 必须 >= 1")
            .Validate(o => o.LoginAuditRetentionDays >= 1, "JobRetention:LoginAuditRetentionDays 必须 >= 1")
            .Validate(o => o.LoginRiskRetentionDays >= 1, "JobRetention:LoginRiskRetentionDays 必须 >= 1")
            .ValidateOnStart();
        services.AddSingleton<WorkerConcurrencyManager>();
        services.AddTransient(typeof(LeasedJobExecutor<>));
        services.AddScoped<IJobRetentionService, JobRetentionService>();
        if (registerWorkerHostedServices)
        {
            services.AddHostedService<WorkerBacklogMetricsWorker>();
            services.AddHostedService<JobRetentionWorker>();
        }
        return services;
    }
}
