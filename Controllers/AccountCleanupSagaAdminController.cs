using System.Security.Claims;
using Core.Interfaces;
using Core.Models.Export;
using Core.Models.Security;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Server.Controllers;

/// <summary>
/// 账号注销清理状态与修复中心（管理员）。
/// 鉴权：<c>[Authorize(Roles = "Admin")]</c>（与 EmailOutbox / Users 管理端一致）。
/// </summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/account-cleanup-saga")]
public sealed class AccountCleanupSagaAdminController(
    UserDbContext db,
    IAccountCleanupSagaService sagaService) : ControllerBase
{
    /// <summary>
    /// 列表：status=Pending|Completed|Failed|DeadLetter；可选 userId；offset/limit 分页。
    /// </summary>
    [HttpGet]
    public Task<AccountCleanupSagaListResponse> List(
        [FromQuery] string? status = null,
        [FromQuery] long? userId = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
        => sagaService.ListAsync(status, userId, offset, limit, cancellationToken);

    /// <summary>单用户 Saga 状态（含死信 / Outbox 字段）。</summary>
    [HttpGet("{userId:long}")]
    public async Task<ActionResult<AccountCleanupSagaItemDto>> GetStatus(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var item = await sagaService.GetStatusAsync(userId, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpGet("failed")]
    public async Task<IActionResult> ListFailed(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var page = await sagaService.ListAsync(
            AccountCleanupSagaStatus.Failed,
            userId: null,
            offset: 0,
            limit,
            cancellationToken);
        return Ok(page.Items);
    }

    [HttpGet("dead-letters")]
    public async Task<IActionResult> ListDeadLetters(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);
        var items = await db.AccountCleanupDeadLetters.AsNoTracking()
            .OrderByDescending(x => x.Id)
            .Skip(offset)
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

    public sealed record AdminActionRequest(string? Reason);

    /// <summary>人工重放：Pending/Failed → 重新投递；Completed → 409。</summary>
    [HttpPost("{userId:long}/replay")]
    public async Task<IActionResult> Replay(
        long userId,
        [FromBody] AdminActionRequest? body,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdminId(out var adminId))
            return Unauthorized();

        var before = await sagaService.GetStatusAsync(userId, cancellationToken);
        var result = await sagaService.TryReplayAsync(userId, cancellationToken);
        if (result.Outcome == AccountCleanupReplayOutcome.Replayed)
        {
            await WriteAuditAsync(
                adminId,
                userId,
                "AccountCleanupSagaReplay",
                body?.Reason,
                $"beforeStatus={before?.SagaStatus ?? "none"};afterStatus={result.Item?.SagaStatus ?? "Pending"};outcome={result.Outcome}",
                cancellationToken);
        }

        return result.Outcome switch
        {
            AccountCleanupReplayOutcome.Replayed => Ok(result),
            AccountCleanupReplayOutcome.AlreadyCompleted => Conflict(result),
            AccountCleanupReplayOutcome.InvalidUser => BadRequest(result),
            _ => NotFound(result),
        };
    }

    /// <summary>对账：Inbox Completed → 标完成；Outbox Dead → 标 Failed。</summary>
    [HttpPost("{userId:long}/reconcile")]
    public async Task<IActionResult> Reconcile(
        long userId,
        [FromBody] AdminActionRequest? body,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdminId(out var adminId))
            return Unauthorized();

        var before = await sagaService.GetStatusAsync(userId, cancellationToken);
        var result = await sagaService.TryReconcileAsync(userId, cancellationToken);
        if (result.Outcome is not (AccountCleanupReconcileOutcome.NotFound
            or AccountCleanupReconcileOutcome.InvalidUser))
        {
            await WriteAuditAsync(
                adminId,
                userId,
                "AccountCleanupSagaReconcile",
                body?.Reason,
                $"beforeStatus={before?.SagaStatus ?? "none"};outcome={result.Outcome};afterStatus={result.Item?.SagaStatus ?? "n/a"}",
                cancellationToken);
        }

        return result.Outcome switch
        {
            AccountCleanupReconcileOutcome.NotFound => NotFound(result),
            AccountCleanupReconcileOutcome.InvalidUser => BadRequest(result),
            _ => Ok(result),
        };
    }

    /// <summary>从死信条目定位用户后安全重放（Completed 仍拒绝）。</summary>
    [HttpPost("dead-letters/{id:long}/replay")]
    public async Task<IActionResult> ReplayFromDeadLetter(
        long id,
        [FromBody] AdminActionRequest? body,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdminId(out var adminId))
            return Unauthorized();

        var dlq = await db.AccountCleanupDeadLetters.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (dlq is null)
            return NotFound(new { Message = "未找到死信" });

        var before = await sagaService.GetStatusAsync(dlq.UserId, cancellationToken);
        var result = await sagaService.TryReplayAsync(dlq.UserId, cancellationToken);
        if (result.Outcome == AccountCleanupReplayOutcome.Replayed)
        {
            await WriteAuditAsync(
                adminId,
                dlq.UserId,
                "AccountCleanupSagaReplayFromDeadLetter",
                body?.Reason,
                $"deadLetterId={id};beforeStatus={before?.SagaStatus ?? "none"};outcome={result.Outcome}",
                cancellationToken);
        }

        return result.Outcome switch
        {
            AccountCleanupReplayOutcome.Replayed => Ok(result),
            AccountCleanupReplayOutcome.AlreadyCompleted => Conflict(result),
            AccountCleanupReplayOutcome.InvalidUser => BadRequest(result),
            _ => NotFound(result),
        };
    }

    private bool TryGetAdminId(out long adminId)
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? User.FindFirstValue(ClaimTypes.Name)
                  ?? User.FindFirstValue("sub");
        return long.TryParse(raw, out adminId);
    }

    private async Task WriteAuditAsync(
        long adminId,
        long? targetUserId,
        string action,
        string? reason,
        string detail,
        CancellationToken cancellationToken)
    {
        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            AdminUserId = adminId,
            TargetUserId = targetUserId,
            Action = action,
            Reason = reason,
            Detail = detail,
            ClientIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
