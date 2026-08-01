using System.Collections.Generic;
using Core.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.Validation;

/// <summary>
/// 校验 <see cref="DataExportStorageOptions"/>：作业生命周期、清理、加密分块与聊天导出参数。
/// </summary>
public sealed class DataExportStorageOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<DataExportStorageOptions>
{
    public string? Name { get; } = null;

    public ValidateOptionsResult Validate(string? name, DataExportStorageOptions options)
    {
        var failures = new List<string>();

        if (options is null)
        {
            return ValidateOptionsResult.Fail("DataExportStorageOptions 不能为 null。");
        }

        if (!string.Equals(options.Provider, "Local", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Provider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("DataExport:Provider 必须为 Local 或 S3。");
        }
        if (environment.IsProduction()
            && !string.Equals(options.Provider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("生产环境 DataExport:Provider 必须为 S3，以保证多实例共享和重启后可恢复。");
        }

        if (string.IsNullOrWhiteSpace(options.LocalRootPath))
            failures.Add("DataExport:LocalRootPath 不能为空。");


        if (options.JobTtlHours <= 0)
        {
            failures.Add("DataExport:JobTtlHours 必须大于 0。");
        }

        if (options.LeaseSeconds <= 0)
        {
            failures.Add("DataExport:LeaseSeconds 必须大于 0。");
        }

        if (options.PollIntervalMilliseconds <= 0)
        {
            failures.Add("DataExport:PollIntervalMilliseconds 必须大于 0。");
        }

        if (options.CleanupIntervalMinutes <= 0)
        {
            failures.Add("DataExport:CleanupIntervalMinutes 必须大于 0。");
        }

        if (options.MaxBlobDeleteAttempts <= 0)
        {
            failures.Add("DataExport:MaxBlobDeleteAttempts 必须大于 0。");
        }

        if (options.EncryptChunkBytes <= 0)
        {
            failures.Add("DataExport:EncryptChunkBytes 必须大于 0。");
        }

        if (options.ChatExportPageSize < 1 || options.ChatExportPageSize > 500)
        {
            failures.Add("DataExport:ChatExportPageSize 必须在 1 到 500 之间。");
        }

        if (options.ChatExportMaxMessages <= 0)
        {
            failures.Add("DataExport:ChatExportMaxMessages 必须大于 0。");
        }

        if (options.ChatExportMaxAttachmentUrls <= 0)
        {
            failures.Add("DataExport:ChatExportMaxAttachmentUrls 必须大于 0。");
        }

        if (options.ChatExportUrlScanMaxContentChars <= 0)
        {
            failures.Add("DataExport:ChatExportUrlScanMaxContentChars 必须大于 0。");
        }

        if (string.Equals(options.Provider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.S3Bucket))
                failures.Add("DataExport:S3Bucket 在 Provider=S3 时不能为空。");

            if (!string.Equals(options.S3SseMode, "SSE-S3", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(options.S3SseMode, "SSE-KMS", StringComparison.OrdinalIgnoreCase))
                failures.Add("DataExport:S3SseMode 必须为 SSE-S3 或 SSE-KMS。");

            if (string.Equals(options.S3SseMode, "SSE-KMS", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(options.S3KmsKeyId))
                failures.Add("DataExport:S3KmsKeyId 在 S3SseMode=SSE-KMS 时不能为空。");

            if (options.EncryptAtRest)
                failures.Add("DataExport:Provider=S3 时应关闭 EncryptAtRest，使用桶级 SSE。");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
