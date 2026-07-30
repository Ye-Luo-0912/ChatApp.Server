using System.Collections.Generic;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Validation;

/// <summary>
/// 校验 <see cref="PasswordHashingOptions"/>：BCrypt 并发上限与闸门等待超时。
/// </summary>
public sealed class PasswordHashingOptionsValidator : IValidateOptions<PasswordHashingOptions>
{
    public string? Name { get; } = null;

    public ValidateOptionsResult Validate(string? name, PasswordHashingOptions options)
    {
        var failures = new List<string>();

        if (options is null)
        {
            return ValidateOptionsResult.Fail("PasswordHashingOptions 不能为 null。");
        }

        if (options.MaxConcurrentOperations <= 0)
        {
            failures.Add("PasswordHashing:MaxConcurrentOperations 必须大于 0。");
        }

        if (options.AcquireTimeoutMilliseconds < 0)
        {
            failures.Add("PasswordHashing:AcquireTimeoutMilliseconds 必须 >= 0。");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
