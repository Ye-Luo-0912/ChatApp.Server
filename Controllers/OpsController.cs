using Core.Interfaces;
using Core.Models.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.Server.Controllers;

/// <summary>
/// 附件运维只读查询（管理员）。生产请继续用 Admin 角色门禁。
/// 相关指标：<c>attachment.blob_delete</c> / <c>attachment.scan</c> /
/// <c>attachment.pending_delete</c> / <c>attachment.pending_scan</c>（meter: Infrastructure.Attachments）。
/// </summary>
[ApiController]
[Authorize(Policy = ChatApp.Server.Authorization.AuthoritativeAdminAuthorization.PolicyName)]
[Route("api/admin/ops")]
public sealed class OpsController(IAttachmentOpsAdminService ops) : ControllerBase
{
    /// <summary>
    /// 孤儿/卡住扫描：Confirmed 未绑定超龄、Ticketed/Uploaded 超龄、Scanning 超阈值。
    /// </summary>
    [HttpGet("attachment-orphans")]
    public Task<AttachmentOpsOrphansDto> AttachmentOrphans(CancellationToken cancellationToken)
        => ops.GetOrphansAsync(cancellationToken);

    /// <summary>删除墓碑失败/高重试 Pending 汇总 + 最差样例。</summary>
    [HttpGet("attachment-delete-failures")]
    public Task<AttachmentOpsDeleteFailuresDto> AttachmentDeleteFailures(CancellationToken cancellationToken)
        => ops.GetDeleteFailuresAsync(cancellationToken);

    /// <summary>扫描作业积压：Pending/Processing/DeadLetter 等 + 最老年龄。</summary>
    [HttpGet("attachment-scan-backlog")]
    public Task<AttachmentOpsScanBacklogDto> AttachmentScanBacklog(CancellationToken cancellationToken)
        => ops.GetScanBacklogAsync(cancellationToken);

    /// <summary>读取指定附件的逐次扫描引擎、版本、判定和原因审计。</summary>
    [HttpGet("attachments/{attachmentId}/scan-audits")]
    public Task<IReadOnlyList<AttachmentScanAuditDto>> AttachmentScanAudits(
        string attachmentId,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
        => ops.GetScanAuditsAsync(attachmentId, limit, cancellationToken);

    /// <summary>廉价提示：Active size 汇总、下载票 TTL、相关 metric 名（不做 Redis KEYS）。</summary>
    [HttpGet("attachment-hints")]
    public Task<AttachmentOpsHintsDto> AttachmentHints(CancellationToken cancellationToken)
        => ops.GetHintsAsync(cancellationToken);

    [HttpPost("attachments/{attachmentId}/rescan")]
    public async Task<IActionResult> Rescan(
        string attachmentId,
        [FromBody] AttachmentOpsActionRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdminId(out var adminUserId))
            return Unauthorized();
        var ok = await ops.RescanAsync(
            adminUserId, attachmentId, request?.Reason, cancellationToken);
        return ok ? Accepted(new { AttachmentId = attachmentId, Status = "Scanning" }) : NotFound();
    }

    [HttpPost("attachments/{attachmentId}/delete")]
    public async Task<IActionResult> Delete(
        string attachmentId,
        [FromBody] AttachmentOpsActionRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdminId(out var adminUserId))
            return Unauthorized();
        var ok = await ops.DeleteAsync(
            adminUserId, attachmentId, request?.Reason, cancellationToken);
        return ok ? Accepted(new { AttachmentId = attachmentId, Status = "DeleteQueued" }) : NotFound();
    }

    [HttpPost("attachments/{attachmentId}/release")]
    public async Task<IActionResult> Release(
        string attachmentId,
        [FromBody] AttachmentOpsActionRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdminId(out var adminUserId))
            return Unauthorized();
        var ok = await ops.ReleaseAsync(
            adminUserId, attachmentId, request?.Reason, cancellationToken);
        return ok ? Ok(new { AttachmentId = attachmentId, Status = "Confirmed" }) : NotFound();
    }

    private bool TryGetAdminId(out long adminUserId)
        => long.TryParse(
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
            out adminUserId);
}
