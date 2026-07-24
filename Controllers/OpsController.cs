using Core.Models.Export;
using Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.Server.Controllers;

/// <summary>
/// 附件运维只读查询（管理员）。生产请继续用 Admin 角色门禁。
/// 相关指标：<c>attachment.blob_delete</c> / <c>attachment.scan</c> /
/// <c>attachment.pending_delete</c> / <c>attachment.pending_scan</c>（meter: Infrastructure.Attachments）。
/// </summary>
[ApiController]
[Authorize(Roles = "Admin")]
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

    /// <summary>廉价提示：Active size 汇总、下载票 TTL、相关 metric 名（不做 Redis KEYS）。</summary>
    [HttpGet("attachment-hints")]
    public Task<AttachmentOpsHintsDto> AttachmentHints(CancellationToken cancellationToken)
        => ops.GetHintsAsync(cancellationToken);
}
