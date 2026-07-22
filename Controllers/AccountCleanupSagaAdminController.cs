using Core.Models.Export;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Core.Interfaces;

namespace ChatApp.Server.Controllers;

/// <summary>
/// 账号清理 Saga 死信查看与人工重放（管理员）。
/// </summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/account-cleanup-saga")]
public sealed class AccountCleanupSagaAdminController(
    UserDbContext db,
    IAccountCleanupSagaService sagaService) : ControllerBase
{
    [HttpGet("failed")]
    public async Task<IActionResult> ListFailed(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var items = await db.AccountCleanupSagas.AsNoTracking()
            .Where(x => x.Status == AccountCleanupSagaStatus.Failed)
            .OrderByDescending(x => x.CompletedAt ?? x.CreatedAt)
            .Take(limit)
            .Select(x => new
            {
                x.UserId,
                x.EventId,
                x.Status,
                x.CreatedAt,
                x.CompletedAt,
                x.LastError,
            })
            .ToListAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("dead-letters")]
    public async Task<IActionResult> ListDeadLetters(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var items = await db.AccountCleanupDeadLetters.AsNoTracking()
            .OrderByDescending(x => x.Id)
            .Take(limit)
            .Select(x => new
            {
                x.Id,
                x.EventId,
                x.UserId,
                x.ReasonCode,
                x.Reason,
                x.DeliveryCount,
                x.CreatedAt,
            })
            .ToListAsync(cancellationToken);
        return Ok(items);
    }

    [HttpPost("{userId:long}/replay")]
    public async Task<IActionResult> Replay(long userId, CancellationToken cancellationToken)
    {
        var ok = await sagaService.TryReplayAsync(userId, cancellationToken);
        return ok ? Ok(new { Message = "已重新投递 UserAccountDeleted" }) : NotFound();
    }
}
