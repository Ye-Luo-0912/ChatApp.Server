using System.Globalization;
using System.Security.Claims;
using Core.Interfaces;
using Core.Settings;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatApp.Server.RateLimiting;

/// <summary>一个限流维度：独立分区键，与同一策略内其他维度共享限额/窗口，任一超限即拒。</summary>
public sealed record RateLimitDimension(string KeySuffix, Func<HttpContext, ValueTask<string?>> ExtractKeyAsync);

/// <summary>限流策略：限额、窗口、失败策略与多个独立维度。</summary>
public sealed record RateLimitPolicy(
    string Name, int PermitLimit, TimeSpan Window, bool FailOpen, IReadOnlyList<RateLimitDimension> Dimensions);

/// <summary>按策略名解析 <see cref="RateLimitPolicy"/>。</summary>
public interface IRateLimitPolicyProvider
{
    RateLimitPolicy? Get(string policyName);
}
/// <summary>从 <see cref="RateLimitingOptions"/> 构建限流策略；单例。</summary>
public sealed class RateLimitPolicyProvider : IRateLimitPolicyProvider
{
    private readonly Dictionary<string, RateLimitPolicy> _policies;

    public RateLimitPolicyProvider(IOptions<RateLimitingOptions> options)
    {
        var rate = options.Value;
        var failOpen = rate.FailOpenWhenRedisUnavailable;
        var policies = new Dictionary<string, RateLimitPolicy>(StringComparer.Ordinal);

        // IP/device stay in the pre-MVC middleware. Account dimensions are acquired
        // by AccountRateLimitActionFilter after model binding, so request JSON is
        // parsed exactly once by MVC.
        policies["auth-login"] = new RateLimitPolicy(
            "auth-login", rate.AuthLoginPermitLimit,
            TimeSpan.FromSeconds(Math.Max(1, rate.AuthLoginWindowSeconds)), failOpen,
            [
                new("ip", ExtractIpAsync),
                new("dev", ExtractDeviceAsync),
            ]);

        policies["auth-register"] = new RateLimitPolicy(
            "auth-register", rate.AuthRegisterPermitLimit,
            TimeSpan.FromSeconds(Math.Max(1, rate.AuthRegisterWindowSeconds)), failOpen,
            [
                new("ip", ExtractIpAsync),
                new("dev", ExtractDeviceAsync),
            ]);

        policies["auth-refresh"] = new RateLimitPolicy(
            "auth-refresh", rate.AuthRefreshPermitLimit,
            TimeSpan.FromSeconds(Math.Max(1, rate.AuthRefreshWindowSeconds)), failOpen,
            [new("ip", ExtractIpAsync)]);

        policies["auth-email"] = new RateLimitPolicy(
            "auth-email", rate.AuthEmailPermitLimit,
            TimeSpan.FromSeconds(Math.Max(1, rate.AuthEmailWindowSeconds)), failOpen,
            [new("ip", ExtractIpAsync)]);

        // 已认证用户按 userId 分区；未认证回退 IP（仍受保护）。
        policies["user-email-change"] = new RateLimitPolicy(
            "user-email-change", rate.UserEmailChangePermitLimit,
            TimeSpan.FromSeconds(Math.Max(1, rate.UserEmailChangeWindowSeconds)), failOpen,
            [new("k", ExtractUserOrIpAsync)]);

        policies["user-sensitive"] = new RateLimitPolicy(
            "user-sensitive", rate.UserSensitivePermitLimit,
            TimeSpan.FromSeconds(Math.Max(1, rate.UserSensitiveWindowSeconds)), failOpen,
            [new("k", ExtractUserOrIpAsync)]);

        _policies = policies;
    }

    public RateLimitPolicy? Get(string policyName) =>
        _policies.TryGetValue(policyName, out var p) ? p : null;

    private static ValueTask<string?> ExtractIpAsync(HttpContext ctx)
        => new(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    private static ValueTask<string?> ExtractDeviceAsync(HttpContext ctx)
    {
        var device = ctx.RequestServices.GetService<IDeviceInfo>()?.GetDeviceId();
        return new(string.IsNullOrWhiteSpace(device) ? null : device);
    }

    private static ValueTask<string?> ExtractUserOrIpAsync(HttpContext ctx)
    {
        var userId = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(userId))
            return new("uid:" + userId);
        return new("ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"));
    }
}
/// <summary>
/// 单例分布式限流中间件：读取端点上的 [EnableRateLimiting] 元数据，
/// 按策略维度调用 <see cref="IDistributedRateLimiter"/>（单例，无每分区本地对象）。
/// <para>必须在 UseAuthentication 之后调用，使已认证接口可按用户 Claim 分区。</para>
/// </summary>
public sealed class DistributedRateLimitingMiddleware(
    RequestDelegate next,
    IDistributedRateLimiter limiter,
    IRateLimitPolicyProvider policyProvider,
    RateLimitDimensionKeyHasher keyHasher,
    ILogger<DistributedRateLimitingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (endpoint.Metadata.GetMetadata<DisableRateLimitingAttribute>() is not null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var attr = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>();
        if (attr is null || string.IsNullOrEmpty(attr.PolicyName))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var policy = policyProvider.Get(attr.PolicyName);
        if (policy is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var ct = context.RequestAborted;
        var partitionKeys = new List<string>(policy.Dimensions.Count);
        foreach (var dim in policy.Dimensions)
        {
            string? component;
            try
            {
                component = await dim.ExtractKeyAsync(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "限流维度提取失败 Policy={Policy} Suffix={Suffix}", policy.Name, dim.KeySuffix);
                continue;
            }

            if (string.IsNullOrEmpty(component))
                continue;

            partitionKeys.Add($"{dim.KeySuffix}:{keyHasher.Hash(component)}");
        }

        var result = await limiter.TryAcquireAsync(
                policy.Name, partitionKeys, policy.PermitLimit, policy.Window, policy.FailOpen, ct)
            .ConfigureAwait(false);

        if (!result.Allowed)
        {
            await WriteRejectionAsync(context, result.RetryAfter, ct).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private static async Task WriteRejectionAsync(HttpContext context, TimeSpan? retryAfter, CancellationToken ct)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (retryAfter is { } ra)
        {
            var secs = Math.Max(1, (int)Math.Ceiling(ra.TotalSeconds));
            context.Response.Headers.RetryAfter = secs.ToString(CultureInfo.InvariantCulture);
        }
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(
            new { error = 429, message = "请求过于频繁，请稍后再试" }, ct)
            .ConfigureAwait(false);
    }
}

public static class DistributedRateLimitingExtensions
{
    /// <summary>启用单例分布式限流中间件（替代框架内置 UseRateLimiter）。</summary>
    public static IApplicationBuilder UseDistributedRateLimiting(this IApplicationBuilder app)
        => app.UseMiddleware<DistributedRateLimitingMiddleware>();
}
