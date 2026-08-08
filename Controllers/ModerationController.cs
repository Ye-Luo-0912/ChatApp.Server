using Core.Interfaces;
using Core.Models.Moderation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/moderation")]
public sealed class ModerationController(IModerationService moderation) : BaseApiController
{
    public sealed class ReportRequest
    {
        public UserReportTargetType TargetType { get; set; } = UserReportTargetType.User;
        public long? TargetUserId { get; set; }
        public string? TargetMessageId { get; set; }
        public string Reason { get; set; } = "";
        public string? Detail { get; set; }
    }

    public sealed class ReviewRequest
    {
        public UserReportStatus Status { get; set; } = UserReportStatus.Reviewed;
        public DateTimeOffset? BanUntil { get; set; }
        public string? Note { get; set; }
    }

    public sealed class AppealRequest
    {
        public string AppealNote { get; set; } = "";
    }

    [HttpPost("reports")]
    public async Task<IActionResult> Report([FromBody] ReportRequest body, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var result = await moderation.ReportAsync(
            userId, body.TargetType, body.TargetUserId, body.TargetMessageId,
            body.Reason, body.Detail, cancellationToken);
        return result.Succeeded ? Ok(new { Message = "举报已提交" }) : BadRequest(result.Errors);
    }

    [Authorize(Policy = ChatApp.Server.Authorization.AuthoritativeAdminAuthorization.PolicyName)]
    [HttpGet("reports")]
    public async Task<IActionResult> List(
        [FromQuery] UserReportStatus? status = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
        => Ok(await moderation.ListReportsAsync(status, cursor, limit, cancellationToken));

    [Authorize(Policy = ChatApp.Server.Authorization.AuthoritativeAdminAuthorization.PolicyName)]
    [HttpGet("reports/{reportId:long}/evidence")]
    public async Task<IActionResult> Evidence(long reportId, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var adminId))
            return Unauthorized();

        var evidence = await moderation.GetEvidenceAsync(adminId, reportId, cancellationToken);
        return evidence is null ? NotFound() : Ok(evidence);
    }

    [Authorize(Policy = ChatApp.Server.Authorization.AuthoritativeAdminAuthorization.PolicyName)]
    [HttpPost("reports/{reportId:long}/review")]
    public async Task<IActionResult> Review(
        long reportId, [FromBody] ReviewRequest body, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var adminId))
            return Unauthorized();

        var result = await moderation.ReviewReportAsync(
            adminId, reportId, body.Status, body.BanUntil, body.Note, cancellationToken);
        return result.Succeeded ? Ok(new { Message = "已处理" }) : BadRequest(result.Errors);
    }

    [HttpPost("reports/{reportId:long}/appeal")]
    public async Task<IActionResult> Appeal(
        long reportId, [FromBody] AppealRequest body, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var result = await moderation.AppealAsync(userId, reportId, body.AppealNote, cancellationToken);
        return result.Succeeded ? Ok(new { Message = "申诉已提交" }) : BadRequest(result.Errors);
    }
}
