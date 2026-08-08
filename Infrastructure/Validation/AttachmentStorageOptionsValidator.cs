using System.Collections.Generic;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Validation;

/// <summary>
/// 校验 <see cref="AttachmentStorageOptions"/>：存储提供方、容量与生命周期参数，
/// 以及 S3 / Local 各自必需的连接字段。
/// </summary>
public sealed class AttachmentStorageOptionsValidator : IValidateOptions<AttachmentStorageOptions>
{
    public string? Name { get; } = null;

    public ValidateOptionsResult Validate(string? name, AttachmentStorageOptions options)
    {
        var failures = new List<string>();

        if (options is null)
        {
            return ValidateOptionsResult.Fail("AttachmentStorageOptions 不能为 null。");
        }

        if (!string.Equals(options.Provider, "Local", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Provider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("AttachmentStorage:Provider 必须为 \"Local\" 或 \"S3\"（不区分大小写）。");
        }

        if (options.MaxBytes <= 0)
        {
            failures.Add("AttachmentStorage:MaxBytes 必须大于 0。");
        }

        if (options.MaxUnconfirmedObjectsPerUser <= 0)
        {
            failures.Add("AttachmentStorage:MaxUnconfirmedObjectsPerUser 必须大于 0。");
        }

        if (options.MaxStorageBytesPerUser <= 0)
        {
            failures.Add("AttachmentStorage:MaxStorageBytesPerUser 必须大于 0。");
        }

        if (options.MaxBytes > 0 && options.MaxStorageBytesPerUser < options.MaxBytes)
        {
            failures.Add("AttachmentStorage:MaxStorageBytesPerUser 不得小于 MaxBytes。");
        }

        if (options.TicketMinutes <= 0)
        {
            failures.Add("AttachmentStorage:TicketMinutes 必须大于 0。");
        }

        if (options.SignedDownloadMinutes <= 0)
        {
            failures.Add("AttachmentStorage:SignedDownloadMinutes 必须大于 0。");
        }

        if (options.DownloadTicketMinutes <= 0)
        {
            failures.Add("AttachmentStorage:DownloadTicketMinutes 必须大于 0。");
        }

        if (options.MaxDeleteAttempts <= 0)
        {
            failures.Add("AttachmentStorage:MaxDeleteAttempts 必须大于 0。");
        }

        if (options.DeleteBackoffSeconds <= 0)
        {
            failures.Add("AttachmentStorage:DeleteBackoffSeconds 必须大于 0。");
        }

        if (options.DeleteBatchSize <= 0)
        {
            failures.Add("AttachmentStorage:DeleteBatchSize 必须大于 0。");
        }

        if (options.MaxScanAttempts <= 0)
        {
            failures.Add("AttachmentStorage:MaxScanAttempts 必须大于 0。");
        }

        if (options.ScanBackoffSeconds <= 0)
        {
            failures.Add("AttachmentStorage:ScanBackoffSeconds 必须大于 0。");
        }

        if (options.ScanBatchSize <= 0)
        {
            failures.Add("AttachmentStorage:ScanBatchSize 必须大于 0。");
        }

        if (options.ScanAuditRetentionDays <= 0)
        {
            failures.Add("AttachmentStorage:ScanAuditRetentionDays 必须大于 0。");
        }

        if (options.ScanStagingMaxBytes <= 0)
            failures.Add("AttachmentStorage:ScanStagingMaxBytes 必须大于 0。");

        if (options.ScanMaxConcurrentBytes <= 0)
            failures.Add("AttachmentStorage:ScanMaxConcurrentBytes 必须大于 0。");

        if (options.MaxBytes > 0 && options.ScanMaxConcurrentBytes < options.MaxBytes)
            failures.Add("AttachmentStorage:ScanMaxConcurrentBytes 不得小于 MaxBytes。");

        if (options.ScanMaxConcurrentBytes > options.ScanStagingMaxBytes)
            failures.Add("AttachmentStorage:ScanMaxConcurrentBytes 不得大于 ScanStagingMaxBytes。");

        if (options.TmpfsSizeBytes > 0 && options.ScanStagingMaxBytes > options.TmpfsSizeBytes)
            failures.Add("AttachmentStorage:ScanStagingMaxBytes 不得大于 TmpfsSizeBytes。");

        if (string.IsNullOrWhiteSpace(options.ScanStagingRoot))
            failures.Add("AttachmentStorage:ScanStagingRoot 不能为空。");

        if (!string.Equals(options.ScannerProvider, "DenyList", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.ScannerProvider, "ClamAV", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("AttachmentStorage:ScannerProvider 必须为 DenyList 或 ClamAV。");
        }

        if (options.ClamAvPort is < 1 or > 65535)
            failures.Add("AttachmentStorage:ClamAvPort 必须在 1-65535 之间。");

        if (options.ClamAvTimeoutMilliseconds <= 0)
            failures.Add("AttachmentStorage:ClamAvTimeoutMilliseconds 必须大于 0。");

        if (options.ArchiveMaxEntries <= 0)
            failures.Add("AttachmentStorage:ArchiveMaxEntries 必须大于 0。");

        if (options.ArchiveMaxUncompressedBytes <= 0)
            failures.Add("AttachmentStorage:ArchiveMaxUncompressedBytes 必须大于 0。");

        if (options.ArchiveMaxPathDepth <= 0)
            failures.Add("AttachmentStorage:ArchiveMaxPathDepth 必须大于 0。");

        if (options.ArchiveMaxNestingDepth <= 0)
            failures.Add("AttachmentStorage:ArchiveMaxNestingDepth 必须大于 0。");

        if (options.StuckScanningMinutes <= 0)
        {
            failures.Add("AttachmentStorage:StuckScanningMinutes 必须大于 0。");
        }

        if (options.OpsHighDeleteAttemptThreshold <= 0)
        {
            failures.Add("AttachmentStorage:OpsHighDeleteAttemptThreshold 必须大于 0。");
        }

        if (options.OpsSampleLimit < 1 || options.OpsSampleLimit > 20)
        {
            failures.Add("AttachmentStorage:OpsSampleLimit 必须在 1 到 20 之间。");
        }

        if (string.Equals(options.Provider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.S3Bucket))
            {
                failures.Add("AttachmentStorage:S3Bucket 在 Provider=S3 时不能为空。");
            }

            if (!string.Equals(options.S3SseMode, "SSE-S3", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(options.S3SseMode, "SSE-KMS", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("AttachmentStorage:S3SseMode 必须为 SSE-S3 或 SSE-KMS。");
            }

            if (string.Equals(options.S3SseMode, "SSE-KMS", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(options.S3KmsKeyId))
            {
                failures.Add("AttachmentStorage:S3KmsKeyId 在 S3SseMode=SSE-KMS 时不能为空。");
            }
        }

        if (string.Equals(options.Provider, "Local", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.LocalRootPath))
            {
                failures.Add("AttachmentStorage:LocalRootPath 在 Provider=Local 时不能为空。");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
