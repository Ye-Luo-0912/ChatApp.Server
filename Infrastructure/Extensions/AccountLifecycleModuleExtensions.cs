using ChatApp.Realtime.Integration;
using Core.Interfaces;
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
    public static IServiceCollection AddAccountLifecycleModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AccountCleanupSagaOptions>()
            .Bind(config.GetSection(AccountCleanupSagaOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<DataExportStorageOptions>()
            .Bind(config.GetSection(DataExportStorageOptions.SectionName))
            .ValidateOnStart();

        services.AddScoped<IAccountLifecycleService, AccountLifecycleService>();
        services.AddScoped<AccountCleanupSagaService>();
        services.AddScoped<IAccountCleanupSagaService>(sp => sp.GetRequiredService<AccountCleanupSagaService>());
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserAccountService, UserAccountService>();
        services.AddHostedService(sp => new AccountCleanupSagaWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetService<IRealtimeMessageBus>(),
            sp.GetRequiredService<IOptions<AccountCleanupSagaOptions>>(),
            sp.GetRequiredService<ILogger<AccountCleanupSagaWorker>>()));
        services.AddHostedService<AccountDeletionWorker>();
        services.AddHostedService<SecurityEventArchiveWorker>();
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
                    bus);
            }

            return new UnavailableRealtimeChatExportReader();
        });
        services.AddScoped<IDataExportService, DataExportService>();
        services.AddHostedService<DataExportWorker>();

        services.AddSingleton<IValidateOptions<AccountCleanupSagaOptions>, AccountCleanupSagaOptionsValidator>();
        services.AddSingleton<IValidateOptions<DataExportStorageOptions>, DataExportStorageOptionsValidator>();

        return services;
    }
}
