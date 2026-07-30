using System.Collections.Generic;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Validation;

/// <summary>
/// 校验 <see cref="TrustedDeviceOptions"/>：每用户设备上限与 LastSeen 写节流窗口。
/// </summary>
public sealed class TrustedDeviceOptionsValidator : IValidateOptions<TrustedDeviceOptions>
{
    public string? Name { get; } = null;

    public ValidateOptionsResult Validate(string? name, TrustedDeviceOptions options)
    {
        var failures = new List<string>();

        if (options is null)
        {
            return ValidateOptionsResult.Fail("TrustedDeviceOptions 不能为 null。");
        }

        if (options.MaxDevicesPerUser <= 0)
        {
            failures.Add("TrustedDevices:MaxDevicesPerUser 必须大于 0。");
        }

        if (options.LastSeenThrottleHours <= 0)
        {
            failures.Add("TrustedDevices:LastSeenThrottleHours 必须大于 0。");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
