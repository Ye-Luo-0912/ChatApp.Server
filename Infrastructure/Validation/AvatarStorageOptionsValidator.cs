using System.Collections.Generic;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Validation;

/// <summary>
/// 校验 <see cref="AvatarStorageOptions"/>：存储提供方、容量、票期与重编码闸门，
/// 以及 S3 / Local 各自必需的连接字段。
/// </summary>
public sealed class AvatarStorageOptionsValidator : IValidateOptions<AvatarStorageOptions>
{
    public string? Name { get; } = null;

    public ValidateOptionsResult Validate(string? name, AvatarStorageOptions options)
    {
        var failures = new List<string>();

        if (options is null)
        {
            return ValidateOptionsResult.Fail("AvatarStorageOptions 不能为 null。");
        }

        if (!string.Equals(options.Provider, "Local", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Provider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("AvatarStorage:Provider 必须为 \"Local\" 或 \"S3\"（不区分大小写）。");
        }

        if (options.MaxBytes <= 0)
        {
            failures.Add("AvatarStorage:MaxBytes 必须大于 0。");
        }

        if (options.TicketMinutes <= 0)
        {
            failures.Add("AvatarStorage:TicketMinutes 必须大于 0。");
        }

        if (options.ReencodeMaxConcurrency <= 0)
        {
            failures.Add("AvatarStorage:ReencodeMaxConcurrency 必须大于 0。");
        }

        if (options.ReencodeAcquireTimeoutMilliseconds < 0)
        {
            failures.Add("AvatarStorage:ReencodeAcquireTimeoutMilliseconds 必须 >= 0（0 表示一直等待）。");
        }

        if (string.Equals(options.Provider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.S3Bucket))
            {
                failures.Add("AvatarStorage:S3Bucket 在 Provider=S3 时不能为空。");
            }

            if (!string.Equals(options.S3SseMode, "SSE-S3", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(options.S3SseMode, "SSE-KMS", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("AvatarStorage:S3SseMode 必须为 SSE-S3 或 SSE-KMS。");
            }

            if (string.Equals(options.S3SseMode, "SSE-KMS", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(options.S3KmsKeyId))
            {
                failures.Add("AvatarStorage:S3KmsKeyId 在 S3SseMode=SSE-KMS 时不能为空。");
            }
        }

        if (string.Equals(options.Provider, "Local", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.LocalRootPath))
            {
                failures.Add("AvatarStorage:LocalRootPath 在 Provider=Local 时不能为空。");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
