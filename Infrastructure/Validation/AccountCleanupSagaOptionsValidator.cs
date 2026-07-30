using System.Collections.Generic;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Validation;

/// <summary>
/// 校验 <see cref="AccountCleanupSagaOptions"/>：超时、扫描间隔与乱序窗口参数。
/// </summary>
public sealed class AccountCleanupSagaOptionsValidator : IValidateOptions<AccountCleanupSagaOptions>
{
    public string? Name { get; } = null;

    public ValidateOptionsResult Validate(string? name, AccountCleanupSagaOptions options)
    {
        var failures = new List<string>();

        if (options is null)
        {
            return ValidateOptionsResult.Fail("AccountCleanupSagaOptions 不能为 null。");
        }

        if (options.PendingTimeoutHours <= 0)
        {
            failures.Add("AccountCleanupSaga:PendingTimeoutHours 必须大于 0。");
        }

        if (options.StalePollIntervalMinutes <= 0)
        {
            failures.Add("AccountCleanupSaga:StalePollIntervalMinutes 必须大于 0。");
        }

        if (options.MaxMissingSagaDeliveries <= 0)
        {
            failures.Add("AccountCleanupSaga:MaxMissingSagaDeliveries 必须大于 0。");
        }

        if (options.MissingSagaNakDelaySeconds <= 0)
        {
            failures.Add("AccountCleanupSaga:MissingSagaNakDelaySeconds 必须大于 0。");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
