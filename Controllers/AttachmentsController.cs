using Core.Models.Attachment;
using Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ChatApp.Server.Controllers;

/// <summary>正式附件：预签名 → 上传 → 确认；鉴权下载（私有，不经静态文件公开）。</summary>
[ApiController]
[Authorize]
[Route("api/attachments")]
[EnableRateLimiting("user-sensitive")]
public sealed class AttachmentsController(IAttachmentService attachments) : BaseApiController
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
            var ticket = await attachments.PresignAsync(userId, model, cancellationToken);
            return Ok(ticket);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }

    [HttpPut("upload")]
    [RequestSizeLimit(30 * 1024 * 1024)]
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

    [HttpPost("confirm")]
    public async Task<IActionResult> Confirm(
        [FromBody] ConfirmAttachmentRequest model,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var (result, body) = await attachments.ConfirmAsync(userId, model, cancellationToken);
        if (!result.Succeeded)
            return BadRequest(result.Errors);
        return Ok(body);
    }

    [HttpPost("{attachmentId}/abandon")]
    public async Task<IActionResult> Abandon(
        string attachmentId,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var decision = await attachments.AbandonAsync(userId, attachmentId, cancellationToken);
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
    /// 鉴权下载。Bound：须为会话成员；Confirmed 未绑定：仅上传者。
    /// Uploaded/Scanning：409 Conflict。Local 流式返回；S3 默认 302 到短时签名 URL。
    /// </summary>
    [HttpGet("{attachmentId}/download")]
    [HttpGet("{attachmentId}/content")]
    public async Task<IActionResult> Download(
        string attachmentId,
        [FromQuery] string? format,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var (decision, access) = await attachments.AuthorizeDownloadAsync(
            userId, attachmentId, cancellationToken);

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
            AttachmentDownloadDecision.Allowed when access is not null =>
                await ServeContentAsync(access, format, cancellationToken),
            _ => NotFound(),
        };
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

        var read = await attachments.OpenLocalContentAsync(access.ObjectKey, cancellationToken);
        if (read is null)
            return NotFound(new { Message = "附件内容不存在" });

        var contentType = string.IsNullOrWhiteSpace(access.ContentType)
            ? read.ContentType
            : access.ContentType;
        var fileName = access.OriginalName ?? read.FileName ?? access.AttachmentId;
        return File(read.Content, contentType, fileDownloadName: fileName, enableRangeProcessing: true);
    }
}
