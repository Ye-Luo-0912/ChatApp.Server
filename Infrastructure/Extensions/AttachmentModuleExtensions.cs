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
/// 附件与头像存储模块的组合根：本地/S3 存储工厂、清理与扫描 Worker、
/// 下载票据、元数据存储及对应选项绑定与校验。
/// </summary>
public static class AttachmentModuleExtensions
{
    /// <summary>注册附件与头像存储模块。</summary>
    public static IServiceCollection AddAttachmentModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddValidatedOptions<AttachmentStorageOptions, AttachmentStorageOptionsValidator>(
            config, AttachmentStorageOptions.SectionName);
        services.AddValidatedOptions<AvatarStorageOptions, AvatarStorageOptionsValidator>(
            config, AvatarStorageOptions.SectionName);
        services.AddValidatedOptions<ProfileOptions, ProfileOptionsValidator>(
            config, ProfileOptions.SectionName);

        services.AddSingleton<AvatarReencodeMetrics>();
        services.AddSingleton<AvatarReencodeQueue>();
        services.AddSingleton<IAvatarStorage>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AvatarStorageOptions>>().Value;
            if (string.Equals(opts.Provider, "S3", StringComparison.OrdinalIgnoreCase))
                return ActivatorUtilities.CreateInstance<S3AvatarStorage>(sp);
            return ActivatorUtilities.CreateInstance<LocalAvatarStorage>(sp);
        });
        services.AddHostedService<AvatarCleanupWorker>();
        services.AddScoped<IAttachmentBlobDeleteService, AttachmentBlobDeleteService>();
        services.AddSingleton<AttachmentBlobDeleteEnqueuer>();
        services.AddScoped<AttachmentAbandonedAgeSweeper>();
        services.AddScoped<IAttachmentScanProjectionService, AttachmentScanProjectionService>();
        services.AddScoped<IAttachmentScanService, AttachmentScanService>();
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
                return new RealtimeAttachmentMetadataStore(
                    sp.GetRequiredService<IOptions<MessageEvidenceOptions>>(),
                    sp.GetRequiredService<IOptions<DataExportStorageOptions>>(),
                    sp.GetRequiredService<ILogger<RealtimeAttachmentMetadataStore>>());
            }

            return new UnavailableAttachmentMetadataStore();
        });
        services.AddScoped<IAttachmentService, AttachmentService>();
        services.AddHostedService<AttachmentCleanupWorker>();
        services.AddHostedService<AttachmentScanWorker>();
        services.AddHostedService<AttachmentScanProjectionWorker>();
        services.AddScoped<IAttachmentOpsAdminService, AttachmentOpsAdminService>();

        return services;
    }
}
