using System.Security.Claims;
using Core.Interfaces.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ChatApp.Server.Authorization;

/// <summary>
/// Admin endpoints are intentionally stricter than ordinary authenticated
/// reads: role membership and account availability are read from the
/// authoritative PostgreSQL snapshot on every authorization decision.
/// </summary>
public static class AuthoritativeAdminAuthorization
{
    public const string PolicyName = "authoritative-admin";
}

public sealed class AuthoritativeAdminRequirement : IAuthorizationRequirement;

public sealed class AuthoritativeAdminAuthorizationHandler(
    IUserAuthorizationFence authSnapshots,
    IHttpContextAccessor httpContextAccessor,
    ILogger<AuthoritativeAdminAuthorizationHandler> logger)
    : AuthorizationHandler<AuthoritativeAdminRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AuthoritativeAdminRequirement requirement)
    {
        var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!long.TryParse(userIdValue, out var userId) || userId <= 0)
            return;

        try
        {
            var snapshot = await authSnapshots
                .GetAuthoritativeAsync(
                    userId,
                    httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None)
                .ConfigureAwait(false);
            if (snapshot?.IsAllowedAt(DateTimeOffset.UtcNow) != true)
                return;

            if (snapshot.Roles.Any(role =>
                    string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)))
            {
                context.Succeed(requirement);
            }
        }
        catch (OperationCanceledException)
            when (httpContextAccessor.HttpContext?.RequestAborted.IsCancellationRequested == true)
        {
            // The request is already ending; do not grant an authorization
            // decision from an incomplete authoritative read.
        }
        catch (Exception ex)
        {
            // Fail closed. The normal authorization middleware will return a
            // forbidden result rather than accidentally trusting stale claims.
            logger.LogWarning(ex, "管理员权威角色检查失败 UserId={UserId}", userId);
        }
    }
}
