using System.Security.Claims;
using Core.Interfaces;
using Core.Models.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.Server.Controllers;

/// <summary>
/// 邮件 Outbox 死信查看与人工重试（管理员）。
/// </summary>
[ApiController]
[Authorize(Policy = ChatApp.Server.Authorization.AuthoritativeAdminAuthorization.PolicyName)]
[Route("api/admin/email-outbox")]
public sealed class EmailOutboxAdminController(
    IEmailOutboxAdminService outbox,
    IAdminAuditWriter auditWriter) : ControllerBase
{
    [HttpGet("dead")]
    public async Task<IActionResult> ListDead(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return Ok(await outbox.ListDeadAsync(limit, cancellationToken));
    }

    [HttpPost("{id:long}/retry")]
    public async Task<IActionResult> Retry(long id, CancellationToken cancellationToken)
    {
        if (!TryGetAdminId(out var adminId))
            return Unauthorized();

        if (!await outbox.RetryAsync(id, cancellationToken))
            return NotFound();

        await auditWriter.WriteAsync(
            adminId,
            targetUserId: null,
            "EmailOutboxRetry",
            reason: null,
            $"outboxId={id};status=Pending",
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        return Ok(new { Message = "已重新入队" });
    }

    private bool TryGetAdminId(out long adminId)
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? User.FindFirstValue(ClaimTypes.Name)
                  ?? User.FindFirstValue("sub");
        return long.TryParse(raw, out adminId);
    }
}
