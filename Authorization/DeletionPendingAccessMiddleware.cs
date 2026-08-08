using System.Security.Claims;
using Core.Models.Auth;
using Core.Models.Identity;
using Microsoft.AspNetCore.Http;

namespace ChatApp.Server.Authorization;

/// <summary>
/// Enforces the restricted-session product contract in one place. The
/// authentication handler has already validated the durable fence; this
/// middleware only applies endpoint capability metadata and does not query a
/// database.
/// </summary>
public sealed class DeletionPendingAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && string.Equals(
                context.User.FindFirstValue(AuthClaimTypes.AccountState),
                AccountState.DeletionPending.ToString(),
                StringComparison.Ordinal)
            && context.GetEndpoint()?.Metadata.GetMetadata<DeletionPendingAccessAttribute>() is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(
                new
                {
                    Code = "account_deletion_pending",
                    Message = "账号处于注销冷静期，该会话仅可查看注销状态、取消注销、导出数据或登出。",
                },
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
