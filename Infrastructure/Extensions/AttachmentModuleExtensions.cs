using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Services;
using Infrastructure.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.Extensions;

/// <summary>
/// 附件与头像存储模块的组合根：本地/S3 存储工厂、清理与扫描 Worker、
/// 下载票据、元数据存储及对应选项绑定与校验。
/// </summary>
public static class AttachmentModuleExtensions
{
    /// <summary>注册附件与头像存储模块。</summary>
    public static IServiceCollection AddAttachmentModule(this IServiceCollection services, IConfiguration config,
        bool registerWorkerHostedServices)
    {
        services.AddValidatedOptions<AttachmentStorageOptions, AttachmentStorageOptionsValidator>(
            config, AttachmentStorageOptions.SectionName);
        services.AddValidatedOptions<AvatarStorageOptions, AvatarStorageOptionsValidator>(
            config, AvatarStorageOptions.SectionName);
        services.AddValidatedOptions<ProfileOptions, ProfileOptionsValidator>(
            config, ProfileOptions.SectionName);

        services.AddSingleton<AvatarReencodeMetrics>();
        services.AddSingleton<AvatarReencodeQueue>();
        services.AddSingleton<AttachmentScanStagingBudget>();
        services.AddSingleton<IAvatarStorage>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AvatarStorageOptions>>().Value;
            if (string.Equals(opts.Provider, "S3", StringComparison.OrdinalIgnoreCase))
                return ActivatorUtilities.CreateInstance<S3AvatarStorage>(sp);
            return ActivatorUtilities.CreateInstance<LocalAvatarStorage>(sp);
        });
        if (registerWorkerHostedServices)
            services.AddHostedService<AvatarCleanupWorker>();
        services.AddScoped<AttachmentBlobDeleteService>();
        services.AddScoped<IAttachmentBlobDeleteService>(
            sp => sp.GetRequiredService<AttachmentBlobDeleteService>());
        services.AddSingleton<ILeasedJobStore<AttachmentBlobDeleteJob>, AttachmentBlobDeleteJobStore>();
        services.AddSingleton<AttachmentBlobDeleteEnqueuer>();
        services.AddScoped<AttachmentAbandonedAgeSweeper>();
        services.AddScoped<IAttachmentConfirmSagaService, AttachmentConfirmSagaService>();
        services.AddSingleton<ILeasedJobStore<AttachmentConfirmSaga>, AttachmentConfirmSagaJobStore>();
        services.AddScoped<IAvatarFinalizationSagaService, AvatarFinalizationSagaService>();
        services.AddSingleton<ILeasedJobStore<AvatarFinalizationSaga>, AvatarFinalizationSagaJobStore>();
        services.AddScoped<AttachmentScanProjectionService>();
        services.AddScoped<IAttachmentScanProjectionService>(
            sp => sp.GetRequiredService<AttachmentScanProjectionService>());
        services.AddSingleton<ILeasedJobStore<AttachmentScanProjection>, AttachmentScanProjectionJobStore>();
        services.AddScoped<IAttachmentScanService, AttachmentScanService>();
        services.AddSingleton<AttachmentScanJobStore>();
        services.AddSingleton<ILeasedJobStore<AttachmentScanJob>>(sp =>
            sp.GetRequiredService<AttachmentScanJobStore>());
        services.AddSingleton<AttachmentScanEnqueuer>();
        services.AddSingleton<IAttachmentDownloadTicketService, AttachmentDownloadTicketService>();
        services.AddSingleton<IAttachmentContentScanner>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AttachmentStorageOptions>>().Value;
            var policy = ActivatorUtilities.CreateInstance<DenyListAttachmentContentScanner>(sp);
            if (!string.Equals(options.ScannerProvider, "ClamAV", StringComparison.OrdinalIgnoreCase))
                return policy;

            return new CompositeAttachmentContentScanner(
                policy,
                ActivatorUtilities.CreateInstance<ClamAvAttachmentContentScanner>(sp));
        });
        services.AddSingleton<IAttachmentStorage>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AttachmentStorageOptions>>().Value;
            if (string.Equals(opts.Provider, "S3", StringComparison.OrdinalIgnoreCase))
                return ActivatorUtilities.CreateInstance<S3AttachmentStorage>(sp);
            return ActivatorUtilities.CreateInstance<LocalAttachmentStorage>(sp);
        });
        services.AddSingleton<IAttachmentMetadataStore>(sp =>
        {
            var evidence = sp.GetRequiredService<IOptions<MessageEvidenceOptions>>().Value;
            var export = sp.GetRequiredService<IOptions<DataExportStorageOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(export.RealtimeConnectionString)
                || !string.IsNullOrWhiteSpace(evidence.RealtimeConnectionString))
            {
                return ActivatorUtilities.CreateInstance<RealtimeAttachmentMetadataStore>(sp);
            }

            return new UnavailableAttachmentMetadataStore();
        });
        services.AddScoped<IAttachmentService, AttachmentService>();
        if (registerWorkerHostedServices)
        {
            services.AddHostedService<AttachmentCleanupWorker>();
            services.AddHostedService<AttachmentConfirmSagaWorker>();
            services.AddHostedService<AttachmentScanWorker>();
            services.AddHostedService<AttachmentScanProjectionWorker>();
            services.AddHostedService<AvatarFinalizationSagaWorker>();
        }
        services.AddScoped<IAttachmentOpsAdminService, AttachmentOpsAdminService>();

        return services;
    }
}
