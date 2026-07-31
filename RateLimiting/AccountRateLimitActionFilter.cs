using ChatApp.Server.Models.Requests;
using Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;

namespace ChatApp.Server.RateLimiting;

/// <summary>
/// Acquires the account dimension after MVC has bound the request model. This
/// avoids buffering and parsing login/register JSON in a front middleware.
/// </summary>
public sealed class AccountRateLimitActionFilter(
    IDistributedRateLimiter limiter,
    IRateLimitPolicyProvider policyProvider,
    RateLimitDimensionKeyHasher keyHasher) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        var policyName = context.HttpContext.GetEndpoint()?.Metadata
            .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        if (string.IsNullOrWhiteSpace(policyName))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var policy = policyProvider.Get(policyName);
        var account = ExtractAccount(context, policyName);
        if (policy is null || string.IsNullOrWhiteSpace(account))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var result = await limiter.TryAcquireAsync(
                policy.Name,
                [$"acct:{keyHasher.Hash(account)}"],
                policy.PermitLimit,
                policy.Window,
                policy.FailOpen,
                context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (!result.Allowed)
        {
            context.Result = new ObjectResult(new
            {
                error = 429,
                message = "请求过于频繁，请稍后再试",
            })
            {
                StatusCode = StatusCodes.Status429TooManyRequests,
            };
            if (result.RetryAfter is { } retry)
            {
                var seconds = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds));
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            return;
        }

        await next().ConfigureAwait(false);
    }

    private static string? ExtractAccount(ActionExecutingContext context, string policyName)
    {
        if (string.Equals(policyName, "auth-login", StringComparison.Ordinal))
        {
            var request = context.ActionArguments.Values.OfType<LoginRequest>().FirstOrDefault();
            return Normalize(request?.Username);
        }

        if (string.Equals(policyName, "auth-register", StringComparison.Ordinal))
        {
            var request = context.ActionArguments.Values.OfType<RegisterRequest>().FirstOrDefault();
            return Normalize(request?.Email);
        }

        return null;
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
