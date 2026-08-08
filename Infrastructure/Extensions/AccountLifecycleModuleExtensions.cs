using ChatApp.Realtime.Integration;
using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Services;
using Infrastructure.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Extensions;

/// <summary>
/// 账号生命周期模块的组合根：账号清理 Saga、删除/归档 Worker、数据导出、
/// 聊天导出读取器及对应选项绑定与校验。
/// </summary>
public static class AccountLifecycleModuleExtensions
{
    /// <summary>注册账号生命周期模块。</summary>
    public static IServiceCollection AddAccountLifecycleModule(this IServiceCollection services, IConfiguration config,
        bool registerWorkerHostedServices)
    {
        services.AddValidatedOptions<AccountCleanupSagaOptions, AccountCleanupSagaOptionsValidator>(
            config, AccountCleanupSagaOptions.SectionName);
        services.AddValidatedOptions<DataExportStorageOptions, DataExportStorageOptionsValidator>(
            config, DataExportStorageOptions.SectionName);

        services.AddScoped<AccountLifecycleService>();
        services.AddScoped<IAccountLifecycleService>(
            sp => sp.GetRequiredService<AccountLifecycleService>());
        services.AddSingleton<ILeasedJobStore<AccountDeletionJob>, AccountDeletionJobStore>();
        services.AddScoped<AccountCleanupSagaService>();
        services.AddScoped<IAccountCleanupSagaService>(sp => sp.GetRequiredService<AccountCleanupSagaService>());
        services.AddScoped<IDeadLetterAdminService, DeadLetterAdminService>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserAccountService, UserAccountService>();
        if (registerWorkerHostedServices)
        {
            services.AddHostedService(sp => new AccountCleanupSagaWorker(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetService<IRealtimeMessageBus>(),
                sp.GetRequiredService<IOptions<AccountCleanupSagaOptions>>(),
                sp.GetRequiredService<ILogger<AccountCleanupSagaWorker>>()));
            services.AddHostedService<AccountDeletionWorker>();
            services.AddHostedService<SecurityEventArchiveWorker>();
        }
        services.AddSingleton<IDataExportBlobStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DataExportStorageOptions>>().Value;
            return string.Equals(options.Provider, "S3", StringComparison.OrdinalIgnoreCase)
                ? ActivatorUtilities.CreateInstance<S3DataExportBlobStore>(sp)
                : ActivatorUtilities.CreateInstance<LocalDataExportBlobStore>(sp);
        });
        services.AddSingleton<IRealtimeChatExportReader>(sp =>
        {
            var evidence = sp.GetRequiredService<IOptions<MessageEvidenceOptions>>().Value;
            var export = sp.GetRequiredService<IOptions<DataExportStorageOptions>>().Value;
            var bus = sp.GetService<IRealtimeMessageBus>();
            if (!string.IsNullOrWhiteSpace(export.RealtimeConnectionString)
                || !string.IsNullOrWhiteSpace(evidence.RealtimeConnectionString)
                || bus is not null)
            {
                return new RealtimeChatExportReader(
                    sp.GetRequiredService<IOptions<MessageEvidenceOptions>>(),
                    sp.GetRequiredService<IOptions<DataExportStorageOptions>>(),
                    sp.GetRequiredService<ILogger<RealtimeChatExportReader>>(),
                    bus,
                    sp.GetRequiredService<RealtimePostgresDataSource>());
            }

            return new UnavailableRealtimeChatExportReader();
        });
        services.AddScoped<IDataExportService, DataExportService>();
        services.AddSingleton<DataExportStagingBudget>();
        services.AddSingleton<DataExportJobStore>();
        services.AddSingleton<ILeasedJobStore<DataExportJob>>(sp =>
            sp.GetRequiredService<DataExportJobStore>());
        if (registerWorkerHostedServices)
        {
            // DataExportWorker keeps a legacy test/factory constructor for
            // focused tests. Resolve the production kernel explicitly so the
            // default DI constructor selector can never see two candidates.
            services.AddHostedService(sp => new DataExportWorker(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<DataExportJobStore>(),
                sp.GetRequiredService<Infrastructure.Diagnostics.LeasedJobExecutor<DataExportJob>>(),
                sp.GetRequiredService<IOptions<DataExportStorageOptions>>(),
                sp.GetRequiredService<IOptions<WorkerConcurrencyOptions>>(),
                sp.GetRequiredService<ILogger<DataExportWorker>>()));
        }

        return services;
    }
}
