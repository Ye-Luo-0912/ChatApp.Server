using System.Collections.Generic;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Validation;

/// <summary>
/// 校验 <see cref="MessageEvidenceOptions"/>：查询超时、缓存秒数与熔断参数。
/// </summary>
public sealed class MessageEvidenceOptionsValidator : IValidateOptions<MessageEvidenceOptions>
{
    public string? Name { get; } = null;

    public ValidateOptionsResult Validate(string? name, MessageEvidenceOptions options)
    {
        var failures = new List<string>();

        if (options is null)
        {
            return ValidateOptionsResult.Fail("MessageEvidenceOptions 不能为 null。");
        }

        if (options.TimeoutMilliseconds <= 0)
        {
            failures.Add("MessageEvidence:TimeoutMilliseconds 必须大于 0。");
        }

        if (options.CacheSeconds < 0)
        {
            failures.Add("MessageEvidence:CacheSeconds 必须 >= 0。");
        }

        if (options.CircuitBreakerFailureThreshold <= 0)
        {
            failures.Add("MessageEvidence:CircuitBreakerFailureThreshold 必须大于 0。");
        }

        if (options.CircuitBreakerDurationSeconds <= 0)
        {
            failures.Add("MessageEvidence:CircuitBreakerDurationSeconds 必须大于 0。");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
