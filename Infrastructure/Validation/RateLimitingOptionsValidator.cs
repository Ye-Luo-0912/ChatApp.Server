using System.Collections.Generic;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Validation;

/// <summary>
/// 校验 <see cref="RateLimitingOptions"/>：各鉴权与用户操作限流窗口的 PermitLimit / WindowSeconds 必须 > 0。
/// </summary>
public sealed class RateLimitingOptionsValidator : IValidateOptions<RateLimitingOptions>
{
    public string? Name { get; } = null;

    public ValidateOptionsResult Validate(string? name, RateLimitingOptions options)
    {
        var failures = new List<string>();

        if (options is null)
        {
            return ValidateOptionsResult.Fail("RateLimitingOptions 不能为 null。");
        }

        if (options.AuthLoginPermitLimit <= 0)
        {
            failures.Add("RateLimiting:AuthLoginPermitLimit 必须大于 0。");
        }

        if (options.AuthLoginWindowSeconds <= 0)
        {
            failures.Add("RateLimiting:AuthLoginWindowSeconds 必须大于 0。");
        }

        if (options.AuthRegisterPermitLimit <= 0)
        {
            failures.Add("RateLimiting:AuthRegisterPermitLimit 必须大于 0。");
        }

        if (options.AuthRegisterWindowSeconds <= 0)
        {
            failures.Add("RateLimiting:AuthRegisterWindowSeconds 必须大于 0。");
        }

        if (options.AuthRefreshPermitLimit <= 0)
        {
            failures.Add("RateLimiting:AuthRefreshPermitLimit 必须大于 0。");
        }

        if (options.AuthRefreshWindowSeconds <= 0)
        {
            failures.Add("RateLimiting:AuthRefreshWindowSeconds 必须大于 0。");
        }

        if (options.AuthEmailPermitLimit <= 0)
        {
            failures.Add("RateLimiting:AuthEmailPermitLimit 必须大于 0。");
        }

        if (options.AuthEmailWindowSeconds <= 0)
        {
            failures.Add("RateLimiting:AuthEmailWindowSeconds 必须大于 0。");
        }

        if (options.UserEmailChangePermitLimit <= 0)
        {
            failures.Add("RateLimiting:UserEmailChangePermitLimit 必须大于 0。");
        }

        if (options.UserEmailChangeWindowSeconds <= 0)
        {
            failures.Add("RateLimiting:UserEmailChangeWindowSeconds 必须大于 0。");
        }

        if (options.UserSensitivePermitLimit <= 0)
        {
            failures.Add("RateLimiting:UserSensitivePermitLimit 必须大于 0。");
        }

        if (options.UserSensitiveWindowSeconds <= 0)
        {
            failures.Add("RateLimiting:UserSensitiveWindowSeconds 必须大于 0。");
        }

        if (options.ClusterShardCount <= 0 || options.ClusterShardCount > 1024)
        {
            failures.Add("RateLimiting:ClusterShardCount 必须在 1 到 1024 之间。");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
