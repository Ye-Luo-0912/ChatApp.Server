using System.Collections.Generic;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Validation;

/// <summary>
/// 校验 <see cref="NotificationOutboxOptions"/>：批大小、并发、轮询与积压采样参数。
/// </summary>
public sealed class NotificationOutboxOptionsValidator : IValidateOptions<NotificationOutboxOptions>
{
    public string? Name { get; } = null;

    public ValidateOptionsResult Validate(string? name, NotificationOutboxOptions options)
    {
        var failures = new List<string>();

        if (options is null)
        {
            return ValidateOptionsResult.Fail("NotificationOutboxOptions 不能为 null。");
        }

        if (options.BatchSize <= 0)
        {
            failures.Add("NotificationOutbox:BatchSize 必须大于 0。");
        }

        if (options.PollIntervalSeconds <= 0)
        {
            failures.Add("NotificationOutbox:PollIntervalSeconds 必须大于 0。");
        }

        if (options.BacklogSampleSeconds <= 0)
        {
            failures.Add("NotificationOutbox:BacklogSampleSeconds 必须大于 0。");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
