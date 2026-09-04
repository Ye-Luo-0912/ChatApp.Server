using Core.Interfaces;
using Core.Models.Attachment;
using ChatApp.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using AttachmentPresignRequest = ChatApp.Contracts.Http.Attachments.AttachmentPresignRequest;
using ConfirmAttachmentRequest = ChatApp.Contracts.Http.Attachments.ConfirmAttachmentRequest;

namespace ChatApp.Server.Controllers;

/// <summary>正式附件：预签名 → 上传 → 确认；鉴权下载（私有，不经静态文件公开）。</summary>
[ApiController]
[Authorize]
[Route("api/attachments")]
[EnableRateLimiting("user-sensitive")]
public sealed class AttachmentsController(
    IAttachmentService attachments,
    IAttachmentConfirmSagaService confirmSagas,
    ILogger<AttachmentsController> logger) : BaseApiController
{
    [HttpPost("presign")]
    public async Task<IActionResult> Presign(
        [FromBody] AttachmentPresignRequest model,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        try
        {
            var result = await attachments.PresignAsync(
                userId,
                model.ToCoreContract(),
                cancellationToken);
            return result.Status switch
            {
                AttachmentUploadReservationStatus.Reserved when result.Response is not null =>
                    Ok(result.Response.ToHttpContract()),
                AttachmentUploadReservationStatus.UnconfirmedObjectLimitExceeded =>
                    PresignDenied(userId, "pending_limit", detail: null, StatusCode(
                        StatusCodes.Status429TooManyRequests,
                        new
                        {
                            Message = "未确认附件数量已达上限，请完成、放弃或稍后重试",
                            Code = "AttachmentPendingLimitExceeded",
                        })),
                AttachmentUploadReservationStatus.StorageBytesLimitExceeded =>
                    PresignDenied(userId, "storage_limit", detail: null, Conflict(new
                    {
                        Message = "附件存储配额不足",
                        Code = "AttachmentStorageQuotaExceeded",
                    })),
                AttachmentUploadReservationStatus.MetadataUnavailable =>
                    StatusCode(
                        StatusCodes.Status503ServiceUnavailable,
                        new
                        {
                            Message = "附件元数据服务不可用",
                            Code = "AttachmentMetadataUnavailable",
                        }),
                _ => StatusCode(
                    StatusCodes.Status500InternalServerError,
                    new { Message = "附件预签状态异常", Code = "AttachmentPresignUnexpected" }),
            };
        }
        catch (ArgumentException ex)
        {
            return PresignDenied(
                userId, "invalid_argument", ex.Message, BadRequest(new { Message = ex.Message }));
        }
    }

    [HttpPut("upload")]
    [RequestSizeLimit(30 * 1024 * 1024)]
    [RequestTimeout("attachment-upload")]
    public async Task<IActionResult> Upload(
        [FromQuery] string ticket,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        if (string.IsNullOrWhiteSpace(ticket))
            return BadRequest(new { Message = "ticket 不能为空" });

        var contentType = Request.ContentType ?? "application/octet-stream";
        var result = await attachments.UploadAsync(
            userId, ticket, Request.Body, contentType, cancellationToken);
        return result.Succeeded ? Ok(new { Message = "上传成功" }) : BadRequest(result.Errors);
    }

    /// <summary>
    /// 确认对象落盘并入队内容扫描。扫描完成前 status=Scanning，禁止 bind/download。
    /// </summary>
    [HttpPost("confirm")]
    public async Task<IActionResult> Confirm(
        [FromBody] ConfirmAttachmentRequest model,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var (result, body) = await attachments.ConfirmAsync(
            userId,
            model.ToCoreContract(),
            cancellationToken);
        if (!result.Succeeded)
            return BadRequest(result.Errors);
        AttachmentAuditLog.AttachmentConfirmed(
            logger, body?.AttachmentId ?? model.AttachmentId ?? string.Empty, userId);
        return Accepted(body?.ToHttpContract());
    }

    /// <summary>查询确认 Saga；客户端可在扫描和投影期间安全轮询。</summary>
    [HttpGet("{attachmentId}/confirm")]
    public async Task<IActionResult> ConfirmStatus(
        string attachmentId,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var status = await confirmSagas.GetStatusAsync(userId, attachmentId, cancellationToken);
        return status is null ? NotFound() : Ok(status.ToHttpContract());
    }

    /// <summary>查询附件真实生命周期状态，避免客户端猜测扫描/投影进度。</summary>
    [HttpGet("{attachmentId}/status")]
    public async Task<IActionResult> Status(
        string attachmentId,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var status = await attachments.GetStatusAsync(userId, attachmentId, cancellationToken);
        return status is null ? NotFound() : Ok(status);
    }

    [HttpPost("{attachmentId}/abandon")]
    public async Task<IActionResult> Abandon(
        string attachmentId,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var decision = await attachments.AbandonAsync(userId, attachmentId, cancellationToken);
        if (decision == AttachmentDownloadDecision.Allowed)
            AttachmentAuditLog.AttachmentAbandoned(logger, attachmentId, userId);
        return decision switch
        {
            AttachmentDownloadDecision.Allowed => Ok(new { Message = "已放弃附件" }),
            AttachmentDownloadDecision.NotFound => NotFound(new { Message = "附件不存在" }),
            AttachmentDownloadDecision.Forbidden => Forbid(),
            AttachmentDownloadDecision.Unavailable => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { Message = "附件元数据服务不可用" }),
            _ => NotFound(),
        };
    }

    /// <summary>
    /// 签发短时下载票（单次消费，TTL 见 AttachmentStorage:DownloadTicketMinutes）。
    /// 客户端再用 GET download?ticket=... 拉取内容。Realtime downloadApiHint 仍为 attachmentId。
    /// </summary>
    [HttpPost("{attachmentId}/ticket")]
    public async Task<IActionResult> IssueDownloadTicket(
        string attachmentId,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            AttachmentAuditLog.DownloadDenied(logger, attachmentId, "anonymous", "ticket", "anonymous");
            return Unauthorized();
        }

        var (decision, body) = await attachments.IssueDownloadTicketAsync(
            userId, attachmentId, cancellationToken);
        AuditDownloadDenied(attachmentId, userId, "ticket", decision);

        if (decision == AttachmentDownloadDecision.Allowed && body is not null)
            AttachmentAuditLog.DownloadTicketIssued(logger, attachmentId, userId);

        return decision switch
        {
            AttachmentDownloadDecision.NotFound => NotFound(new { Message = "附件不存在" }),
            AttachmentDownloadDecision.Forbidden => Forbid(),
            AttachmentDownloadDecision.NotReady => Conflict(new
            {
                Message = "附件仍在扫描中，暂不可下载",
                Code = "AttachmentNotReady",
            }),
            AttachmentDownloadDecision.Unavailable => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { Message = "附件元数据服务不可用" }),
            AttachmentDownloadDecision.Allowed when body is not null => Ok(body),
            _ => NotFound(),
        };
    }

    /// <summary>
    /// 鉴权下载。Bound：须为会话成员；Confirmed 未绑定：仅上传者。
    /// Uploaded/Scanning：409 Conflict。可选 ?ticket= 短时票（须先 POST /ticket）。
    /// Local 流式返回；S3 默认 302 到短时签名 URL。
    /// </summary>
    [HttpGet("{attachmentId}/download")]
    [HttpGet("{attachmentId}/content")]
    [DisableRequestTimeout]
    public async Task<IActionResult> Download(
        string attachmentId,
        [FromQuery] string? format,
        [FromQuery] string? ticket,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            AttachmentAuditLog.DownloadDenied(logger, attachmentId, "anonymous", "download", "anonymous");
            return Unauthorized();
        }

        var (decision, access) = string.IsNullOrWhiteSpace(ticket)
            ? await attachments.AuthorizeDownloadAsync(userId, attachmentId, cancellationToken)
            : await attachments.AuthorizeDownloadWithTicketAsync(
                userId, attachmentId, ticket, cancellationToken);
        AuditDownloadDenied(attachmentId, userId, "download", decision);

        return decision switch
        {
            AttachmentDownloadDecision.NotFound => NotFound(new { Message = "附件不存在" }),
            AttachmentDownloadDecision.Forbidden => Forbid(),
            AttachmentDownloadDecision.InvalidTicket => Unauthorized(new
            {
                Message = "下载票无效、过期或与用户不匹配",
                Code = "AttachmentTicketInvalid",
            }),
            AttachmentDownloadDecision.NotReady => Conflict(new
            {
                Message = "附件仍在扫描中，暂不可下载",
                Code = "AttachmentNotReady",
            }),
            AttachmentDownloadDecision.Unavailable => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { Message = "附件元数据服务不可用" }),
            AttachmentDownloadDecision.Allowed when access is not null =>
                await ServeContentAsync(access, format, cancellationToken),
            _ => NotFound(),
        };
    }

    /// <summary>预签拒绝审计与响应共用出口：配额类无 attachmentId（上传票已被撤销）。</summary>
    private IActionResult PresignDenied(long userId, string reason, string? detail, IActionResult response)
    {
        AttachmentAuditLog.PresignDenied(logger, userId, reason, detail);
        return response;
    }

    /// <summary>
    /// 4101 只审计隔离边界的否定决策：越权、票据无效、扫描中。
    /// NotFound/Unavailable 与成员隔离无关，不产生审计噪音。
    /// </summary>
    private void AuditDownloadDenied(
        string attachmentId,
        long userId,
        string action,
        AttachmentDownloadDecision decision)
    {
        var code = decision switch
        {
            AttachmentDownloadDecision.Forbidden => "forbidden",
            AttachmentDownloadDecision.InvalidTicket => "invalid_ticket",
            AttachmentDownloadDecision.NotReady => "not_ready",
            _ => null,
        };
        if (code is not null)
            AttachmentAuditLog.DownloadDenied(logger, attachmentId, userId.ToString(), action, code);
    }

    private async Task<IActionResult> ServeContentAsync(
        AttachmentDownloadAccess access,
        string? format,
        CancellationToken cancellationToken)
    {
        var signed = await attachments.CreateSignedDownloadAsync(access.ObjectKey, cancellationToken);
        if (signed is not null)
        {
            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                return Ok(new AttachmentSignedDownloadResponse
                {
                    Url = signed.Url,
                    ExpiresAt = signed.ExpiresAt,
                });
            }

            return Redirect(signed.Url);
        }

        var contentType = string.IsNullOrWhiteSpace(access.ContentType)
            ? "application/octet-stream"
            : access.ContentType;
        var fileName = access.OriginalName ?? access.AttachmentId;

        // 本地落盘：PhysicalFile 由宿主零拷贝发送，避免再经用户态 Stream 缓冲。
        var physicalPath = attachments.TryResolveLocalPhysicalPath(access.ObjectKey);
        if (physicalPath is not null)
            return PhysicalFile(physicalPath, contentType, fileDownloadName: fileName, enableRangeProcessing: true);

        var read = await attachments.OpenLocalContentAsync(access.ObjectKey, cancellationToken);
        if (read is null)
            return NotFound(new { Message = "附件内容不存在" });

        contentType = string.IsNullOrWhiteSpace(access.ContentType)
            ? read.ContentType
            : access.ContentType;
        fileName = access.OriginalName ?? read.FileName ?? access.AttachmentId;
        return File(read.Content, contentType, fileDownloadName: fileName, enableRangeProcessing: true);
    }
}
