using System.Security.Claims;
using Core.Interfaces;
using Core.Models.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.Server.Controllers;

/// <summary>统一后台队列死信运维入口。</summary>
[ApiController]
[Authorize(Policy = ChatApp.Server.Authorization.AuthoritativeAdminAuthorization.PolicyName)]
[Route("api/admin/jobs/dead-letters")]
public sealed class DeadLetterAdminController(
    IDeadLetterAdminService service,
    IAdminAuditWriter auditWriter) : ControllerBase
{
    [HttpGet]
    public Task<DeadLetterPage> List(
        [FromQuery] string? queue = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
        => service.ListAsync(queue, offset, limit, cancellationToken);

    [HttpGet("{queue}/{jobId}")]
    public async Task<ActionResult<DeadLetterItemDto>> Get(
        string queue,
        string jobId,
        CancellationToken cancellationToken)
    {
        var item = await service.GetAsync(queue, jobId, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    public sealed record ActionRequest(string? Reason);

    [HttpPost("{queue}/{jobId}/replay")]
    public Task<IActionResult> Replay(
        string queue,
        string jobId,
        [FromBody] ActionRequest? request,
        CancellationToken cancellationToken)
        => ExecuteActionAsync("replay", queue, jobId, request?.Reason, cancellationToken);

    [HttpPost("{queue}/{jobId}/skip")]
    public Task<IActionResult> Skip(
        string queue,
        string jobId,
        [FromBody] ActionRequest? request,
        CancellationToken cancellationToken)
        => ExecuteActionAsync("skip", queue, jobId, request?.Reason, cancellationToken);

    [HttpPost("{queue}/{jobId}/repair")]
    public Task<IActionResult> Repair(
        string queue,
        string jobId,
        [FromBody] ActionRequest? request,
        CancellationToken cancellationToken)
        => ExecuteActionAsync("repair", queue, jobId, request?.Reason, cancellationToken);

    private async Task<IActionResult> ExecuteActionAsync(
        string action,
        string queue,
        string jobId,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdminId(out var adminUserId))
            return Unauthorized();

        var result = action switch
        {
            "replay" => await service.ReplayAsync(adminUserId, queue, jobId, reason, cancellationToken),
            "skip" => await service.SkipAsync(adminUserId, queue, jobId, reason, cancellationToken),
            _ => await service.RepairAsync(adminUserId, queue, jobId, reason, cancellationToken),
        };

        if (result.Succeeded)
        {
            await auditWriter.WriteAsync(
                adminUserId,
                result.Item?.UserId,
                $"DeadLetter{char.ToUpperInvariant(action[0])}{action[1..]}",
                reason,
                $"queue={queue};jobId={jobId};code={result.Code}",
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            return Ok(result);
        }

        return result.Code switch
        {
            "not_found" => NotFound(result),
            "not_dead" => Conflict(result),
            _ => BadRequest(result),
        };
    }

    private bool TryGetAdminId(out long adminUserId)
        => long.TryParse(
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(ClaimTypes.Name)
            ?? User.FindFirstValue("sub"),
            out adminUserId);
}
