using System.Collections.Generic;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Validation;

/// <summary>
/// 校验 <see cref="DataExportStorageOptions"/>：作业生命周期、清理、加密分块与聊天导出参数。
/// </summary>
public sealed class DataExportStorageOptionsValidator : IValidateOptions<DataExportStorageOptions>
{
    public string? Name { get; } = null;

    public ValidateOptionsResult Validate(string? name, DataExportStorageOptions options)
    {
        var failures = new List<string>();

        if (options is null)
        {
            return ValidateOptionsResult.Fail("DataExportStorageOptions 不能为 null。");
        }

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

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
