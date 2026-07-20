using Core.Models.Email;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Server.Controllers;

/// <summary>
/// 邮件 Outbox 死信查看与人工重试（管理员）。
/// </summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/email-outbox")]
public sealed class EmailOutboxAdminController(UserDbContext db) : ControllerBase
{
    [HttpGet("dead")]
    public async Task<IActionResult> ListDead(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var items = await db.EmailOutbox.AsNoTracking()
            .Where(x => x.Status == EmailOutboxStatus.Dead)
            .OrderByDescending(x => x.UpdatedAt)
            .Take(limit)
            .Select(x => new
            {
                x.Id,
                x.To,
                x.Subject,
                x.EmailType,
                x.AttemptCount,
                x.LastError,
                x.UpdatedAt,
                x.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return Ok(items);
    }

    [HttpPost("{id:long}/retry")]
    public async Task<IActionResult> Retry(long id, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var updated = await db.EmailOutbox
            .Where(x => x.Id == id && (x.Status == EmailOutboxStatus.Dead || x.Status == EmailOutboxStatus.Failed))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, EmailOutboxStatus.Pending)
                .SetProperty(x => x.NextAttemptAt, now)
                .SetProperty(x => x.LockedAt, (DateTime?)null)
                .SetProperty(x => x.LockOwner, (string?)null)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.LastError, (string?)null), cancellationToken);

        return updated == 0 ? NotFound() : Ok(new { Message = "已重新入队" });
    }
}
