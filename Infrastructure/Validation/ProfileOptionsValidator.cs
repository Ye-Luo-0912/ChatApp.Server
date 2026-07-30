using System.Collections.Generic;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Validation;

/// <summary>
/// 校验 <see cref="ProfileOptions"/>：用户名长度上下界与单调性。
/// </summary>
public sealed class ProfileOptionsValidator : IValidateOptions<ProfileOptions>
{
    public string? Name { get; } = null;

    public ValidateOptionsResult Validate(string? name, ProfileOptions options)
    {
        var failures = new List<string>();

        if (options is null)
        {
            return ValidateOptionsResult.Fail("ProfileOptions 不能为 null。");
        }

        if (options.UserNameMinLength < 1)
        {
            failures.Add("Profile:UserNameMinLength 必须 >= 1。");
        }

        if (options.UserNameMaxLength < options.UserNameMinLength)
        {
            failures.Add("Profile:UserNameMaxLength 必须 >= UserNameMinLength。");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
