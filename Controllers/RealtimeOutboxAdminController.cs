using System.Security.Claims;
using Core.Models.Export;
using Core.Models.Security;
using Infrastructure.Data;
using Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Server.Controllers;

/// <summary>
/// Realtime Outbox / DLQ 运维控制台（管理员）。
/// 查看积压、死信失败原因与重试次数，并支持安全重放（仅 Dead → Pending）。
/// </summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/realtime-outbox")]
public sealed class RealtimeOutboxAdminController(
    IRealtimeOutboxAdminService outbox,
    UserDbContext db) : ControllerBase
{
    /// <summary>积压与死信汇总（含最老 Pending 年龄）。</summary>
    [HttpGet("summary")]
    public Task<RealtimeOutboxSummaryDto> Summary(CancellationToken cancellationToken)
        => outbox.GetSummaryAsync(cancellationToken);

    /// <summary>
    /// 列表。status=Pending|Published|Dead（或 0/1/2）；可选 targetUserId、eventType；offset/limit 分页。
    /// </summary>
    [HttpGet]
    public Task<RealtimeOutboxListResponse> List(
        [FromQuery] string? status = null,
        [FromQuery] long? targetUserId = null,
        [FromQuery] short? eventType = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
        => outbox.ListAsync(status, targetUserId, eventType, offset, limit, cancellationToken);

    [HttpGet("{eventId}")]
    public async Task<ActionResult<RealtimeOutboxItemDto>> Get(
        string eventId,
        CancellationToken cancellationToken)
    {
        var item = await outbox.GetAsync(eventId, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    /// <summary>安全重放：仅 Dead 可重置为 Pending；Published/Pending 返回 409。</summary>
    [HttpPost("{eventId}/replay")]
    public async Task<IActionResult> Replay(
        string eventId,
        [FromBody] AdminActionRequest? body,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdminId(out var adminId))
            return Unauthorized();

        var before = await outbox.GetAsync(eventId, cancellationToken);
        var (ok, error) = await outbox.ReplayDeadAsync(eventId, cancellationToken);
        if (ok)
        {
            await WriteAuditAsync(
                adminId,
                before?.TargetUserId,
                "RealtimeOutboxReplay",
                body?.Reason,
                $"eventId={eventId};beforeStatus={before?.StatusName ?? "unknown"};afterStatus=Pending",
                cancellationToken);
            return Ok(new { eventId, status = "pending" });
        }

        return error switch
        {
            "not_found" => NotFound(new { error }),
            "invalid_event_id" => BadRequest(new { error }),
            "already_published" or "not_dead" => Conflict(new { error, eventId }),
            _ => Conflict(new { error, eventId }),
        };
    }

    public sealed record BatchReplayRequest(IReadOnlyList<string>? EventIds, string? Reason);

    [HttpPost("replay-batch")]
    public async Task<ActionResult<RealtimeOutboxBatchReplayResult>> ReplayBatch(
        [FromBody] BatchReplayRequest body,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdminId(out var adminId))
            return Unauthorized();

        var ids = body.EventIds ?? [];
        if (ids.Count == 0)
            return BadRequest(new { error = "empty_event_ids" });

        var result = await outbox.ReplayDeadBatchAsync(ids, cancellationToken);
        await WriteAuditAsync(
            adminId,
            targetUserId: null,
            "RealtimeOutboxReplayBatch",
            body.Reason,
            $"requested={result.Requested};replayed={result.Replayed};skipped={result.Skipped.Count}",
            cancellationToken);
        return Ok(result);
    }

    public sealed record AdminActionRequest(string? Reason);

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
