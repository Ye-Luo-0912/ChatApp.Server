using ChatApp.Server.Models.Requests;
using ChatApp.Server.Authorization;
using ChatApp.Server.Models;
using ChatApp.Contracts.Http.Sessions;
using Core.Interfaces;
using Core.Models.Auth;
using Core.Models.Export;
using Core.Models.User;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ChatApp.Server.Controllers;

/// <summary>
/// 用户资料、安全中心、会话与管理操作。
/// </summary>
[ApiController]
[Authorize]
[Route("api/users")]
public class UsersController(
    IUserAccountService userAccountService,
    IAccountLifecycleService accountLifecycle,
    INotificationQuery notifications,
    IAdminAuditQuery adminAuditQuery,
    ITrustedDeviceService trustedDevices,
    IDataExportService dataExport,
    IDeviceInfo deviceInfo,
    IAvatarFinalizationSagaService avatarFinalization) : BaseApiController
{
    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentUser(CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var user = await userAccountService.GetByIdAsync(userId, cancellationToken);
        return user is not null ? Ok(user) : NotFound();
    }

    [HttpGet("search")]
    public async Task<IActionResult> SearchUsers(
        [FromQuery] string q,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var page = await userAccountService.SearchUsersAsync(q, cursor, limit, cancellationToken);
        return Ok(page);
    }

    [AllowAnonymous]
    [HttpGet("{username}")]
    public async Task<IActionResult> GetUserByName(string username, CancellationToken cancellationToken)
    {
        var user = await userAccountService.GetByUserNameAsync(username, cancellationToken);
        return user is not null
            ? Ok(user)
            : NotFound(new { Message = "用户不存在" });
    }

    [HttpPut("me")]
    public async Task<IActionResult> UpdateCurrentUser(
        [FromBody] UpdateCurrentUserRequest model, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var result = await userAccountService.UpdateAsync(userId, new UpdateProfileRequest
        {
            PhoneNumber = model.PhoneNumber,
            UserName = model.UserName,
            Signature = model.Signature,
            Region = model.Region,
            Birthday = model.Birthday,
            Gender = model.Gender,
            AllowBeSearched = model.AllowBeSearched,
            NotifySecurityEmail = model.NotifySecurityEmail,
        }, cancellationToken);

        if (result is null)
            return NotFound();

        return result.Succeeded ? Ok(new { Message = "更新成功" }) : BadRequest(result.Errors);
    }

    [HttpPost("me/avatar/presign")]
    public async Task<IActionResult> PresignAvatar(
        [FromBody] AvatarPresignRequest model, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        try
        {
            var ticket = await userAccountService.CreateAvatarUploadTicketAsync(
                userId, model.ContentType, model.ContentLength, cancellationToken);
            return ticket is null ? NotFound() : Ok(ticket);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }

    [HttpPut("me/avatar/upload")]
    [RequestSizeLimit(3 * 1024 * 1024)]
    [RequestTimeout("avatar-upload")]
    public async Task<IActionResult> UploadAvatar(
        [FromQuery] string ticket,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        if (string.IsNullOrWhiteSpace(ticket))
            return BadRequest(new { Message = "ticket 不能为空" });

        var contentType = Request.ContentType ?? "application/octet-stream";
        var result = await userAccountService.UploadAvatarBytesAsync(
            userId, ticket, Request.Body, contentType, cancellationToken);
        if (result is null) return NotFound();
        return result.Succeeded ? Ok(new { Message = "上传成功" }) : BadRequest(result.Errors);
    }

    [HttpPost("me/avatar/confirm")]
    public async Task<IActionResult> ConfirmAvatar(
        [FromBody] ConfirmAvatarRequest model, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var (result, response) = await avatarFinalization.RequestAsync(
            userId, model.ObjectKey, model.Ticket, cancellationToken);
        if (!result.Succeeded)
            return BadRequest(result.Errors);
        return Accepted(response);
    }

    [HttpGet("me/avatar/finalization/{sagaId:long}")]
    public async Task<IActionResult> GetAvatarFinalization(
        long sagaId,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var status = await avatarFinalization.GetStatusAsync(userId, sagaId, cancellationToken);
        return status is null ? NotFound() : Ok(status);
    }

    [HttpGet("me/security-events")]
    public async Task<IActionResult> ListSecurityEvents(
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        return Ok(await userAccountService.ListSecurityEventsAsync(userId, cursor, limit, cancellationToken));
    }

    [HttpPost("me/security-events/{eventId:long}/not-me")]
    public async Task<IActionResult> ReportNotMe(long eventId, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var result = await userAccountService.ReportNotMeAsync(userId, eventId, cancellationToken);
        if (result is null) return NotFound();
        return result.Succeeded
            ? Ok(new { Message = "已标记为非本人操作" })
            : BadRequest(result.Errors);
    }

    /// <summary>拒绝可疑登录：仅撤销该事件关联设备会话，不强制改密（与 not-me 区分）。</summary>
    [HttpPost("me/security-events/{eventId:long}/reject")]
    public async Task<IActionResult> RejectSuspiciousLogin(long eventId, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var result = await userAccountService.RejectSuspiciousLoginAsync(userId, eventId, cancellationToken);
        if (result is null) return NotFound();
        return result.Succeeded
            ? Ok(new { Message = "已拒绝该次可疑登录" })
            : BadRequest(result.Errors);
    }

    [HttpPost("me/security-events/{eventId:long}/acknowledge")]
    public async Task<IActionResult> AcknowledgeSecurityEvent(
        long eventId, [FromBody] AcknowledgeSecurityEventRequest? body, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var (result, plainToken) = await trustedDevices.AcknowledgeUnusualLoginAsync(
            userId, eventId, deviceInfo.GetDeviceId(), deviceInfo.GenerateDeviceInfo().IpAddress,
            body?.Password, body?.MfaCode, body?.StepUpToken, cancellationToken);
        return result.Succeeded
            ? Ok(new { Message = "已确认本人操作，并签发可信设备令牌", TrustedDeviceToken = plainToken })
            : BadRequest(result.Errors);
    }

    [HttpGet("me/trusted-devices")]
    public async Task<IActionResult> ListTrustedDevices(CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        return Ok(await trustedDevices.ListAsync(userId, cancellationToken));
    }

    [HttpPost("me/step-up")]
    [EnableRateLimiting("user-sensitive")]
    public async Task<IActionResult> CreateStepUp(
        [FromBody] StepUpRequest body, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var purpose = string.IsNullOrWhiteSpace(body.Purpose)
            ? StepUpPurposes.TrustedDevice
            : body.Purpose.Trim();
        var (result, token) = await trustedDevices.CreateStepUpTokenAsync(
            userId, body.Password, body.MfaCode, purpose, cancellationToken);
        return result.Succeeded
            ? Ok(new
            {
                StepUpToken = token,
                Purpose = purpose,
                ExpiresInSeconds = (int)AuthTimingDefaults.StepUpLifetime.TotalSeconds,
            })
            : BadRequest(result.Errors);
    }

    [HttpPost("me/trusted-devices")]
    [EnableRateLimiting("user-sensitive")]
    public async Task<IActionResult> TrustCurrentDevice(
        [FromBody] TrustDeviceRequest? body, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var (result, plainToken) = await trustedDevices.TrustCurrentAsync(
            userId, deviceInfo.GetDeviceId(), body?.Label, deviceInfo.GenerateDeviceInfo().IpAddress,
            body?.Password, body?.MfaCode, body?.StepUpToken, cancellationToken);
        return result.Succeeded
            ? Ok(new { Message = "设备已信任", TrustedDeviceToken = plainToken })
            : BadRequest(result.Errors);
    }

    [HttpDelete("me/trusted-devices/{id:long}")]
    public async Task<IActionResult> RemoveTrustedDevice(long id, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        var result = await trustedDevices.RemoveAsync(userId, id, cancellationToken);
        return result.Succeeded ? NoContent() : BadRequest(result.Errors);
    }

    [HttpGet("me/notifications")]
    public async Task<IActionResult> ListNotifications(
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        return Ok(await notifications.ListAsync(userId, cursor, limit, cancellationToken));
    }

    [HttpGet("me/notifications/unread-count")]
    public async Task<IActionResult> UnreadNotificationCount(CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        var count = await notifications.CountUnreadAsync(userId, cancellationToken);
        return Ok(new { Count = count });
    }

    [HttpPost("me/notifications/{id:long}/read")]
    public async Task<IActionResult> MarkNotificationRead(long id, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        await notifications.MarkReadAsync(userId, id, cancellationToken);
        return NoContent();
    }

    [HttpPost("me/notifications/read-batch")]
    public async Task<IActionResult> MarkNotificationsReadBatch(
        [FromBody] MarkNotificationsReadRequest body, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        var updated = await notifications.MarkReadBatchAsync(userId, body.Ids ?? [], cancellationToken);
        return Ok(new { Updated = updated });
    }

    [HttpPost("me/deletion/schedule")]
    public async Task<IActionResult> ScheduleDeletion(CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        var result = await accountLifecycle.ScheduleDeletionAsync(userId, cancellationToken);
        return result.Succeeded
            ? Ok(new { Message = "已预约注销，冷静期 14 天后生效" })
            : BadRequest(result.Errors);
    }

    [HttpPost("me/deletion/cancel")]
    [DeletionPendingAccess]
    public async Task<IActionResult> CancelDeletion(CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        var result = await accountLifecycle.CancelDeletionAsync(userId, cancellationToken);
        return result.Succeeded ? Ok(new { Message = "已取消注销" }) : BadRequest(result.Errors);
    }

    [HttpGet("me/deletion")]
    [DeletionPendingAccess]
    public async Task<IActionResult> GetDeletionStatus(CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var status = await accountLifecycle.GetDeletionStatusAsync(userId, cancellationToken);
        return status is null ? NotFound() : Ok(status);
    }

    [HttpGet("me/export")]
    [DeletionPendingAccess]
    public IActionResult ExportDataLegacy()
    {
        // Do not keep a synchronous PII export path alive. It bypasses the
        // asynchronous size/step-up/audit lifecycle and can hold a request
        // together while the complete user record is materialized. Clients
        // must use the bounded, audited export-job flow below.
        Response.Headers["Deprecation"] = "true";
        Response.Headers["Link"] = "</api/users/me/export/jobs>; rel=alternate";
        return StatusCode(
            StatusCodes.Status410Gone,
            new
            {
                Code = "export_endpoint_retired",
                Message = "同步导出接口已停用，请使用需要二次验证的异步导出作业。",
            });
    }

    [HttpPost("me/export/jobs")]
    [DeletionPendingAccess]
    [EnableRateLimiting("user-sensitive")]
    public async Task<IActionResult> StartExportJob(
        [FromBody] StepUpRequest? body, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        var (result, jobId) = await dataExport.EnqueueAsync(
            userId, body?.Password, body?.MfaCode, body?.StepUpToken, cancellationToken);
        if (!result.Succeeded)
            return BadRequest(result.Errors);
        return Accepted(new { JobId = jobId, StatusUrl = $"/api/users/me/export/jobs/{jobId}" });
    }

    [HttpGet("me/export/jobs/{jobId}")]
    [DeletionPendingAccess]
    public async Task<IActionResult> GetExportJob(string jobId, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        var status = await dataExport.GetStatusAsync(userId, jobId, cancellationToken);
        return status is null ? NotFound() : Ok(status);
    }

    [HttpPost("me/export/jobs/{jobId}/cancel")]
    [DeletionPendingAccess]
    public async Task<IActionResult> CancelExportJob(
        string jobId,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var result = await dataExport.CancelAsync(userId, jobId, cancellationToken);
        return result.Succeeded ? Ok(new { JobId = jobId, Status = "Cancelled" }) : Conflict(result.Errors);
    }

    [HttpGet("me/export/jobs/{jobId}/download")]
    [DeletionPendingAccess]
    [DisableRequestTimeout]
    public async Task<IActionResult> DownloadExportJob(string jobId, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        var (stream, fileName, error) = await dataExport.OpenDownloadAsync(userId, jobId, cancellationToken);
        if (stream is null)
        {
            return BadRequest(new
            {
                Code = error ?? "download_failed",
                Message = MapExportDownloadError(error),
            });
        }

        // The job is consumed when this download is successfully opened. The
        // stream remains durable/lease-protected until disposed, and the
        // response supports range processing when the provider exposes a
        // seekable stream.
        return File(
            stream,
            "application/json",
            fileName,
            enableRangeProcessing: true);
    }

    private static string MapExportDownloadError(string? code) => code switch
    {
        DataExportDownloadErrors.JobNotFound => "作业不存在",
        DataExportDownloadErrors.DownloadConsumed => "下载链接已使用",
        DataExportDownloadErrors.Expired => "导出已过期",
        DataExportDownloadErrors.NotReady => "导出尚未就绪",
        DataExportDownloadErrors.BlobMissing => "导出文件缺失",
        DataExportDownloadErrors.Cancelled => "导出已取消",
        _ => "无法下载",
    };

    [HttpPost("me/email/request-change")]
    [EnableRateLimiting("user-email-change")]
    public async Task<IActionResult> RequestEmailChange(
        [FromBody] RequestEmailChangeRequest model, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var result = await userAccountService.RequestEmailChangeAsync(userId, model.NewEmail, cancellationToken);
        if (result is null)
            return NotFound();

        return result.Succeeded
            ? Ok(new { Message = "验证码已发送至新邮箱" })
            : BadRequest(result.Errors);
    }

    [HttpPost("me/phone/request-change")]
    [EnableRateLimiting("user-sensitive")]
    public async Task<IActionResult> RequestPhoneChange(
        [FromBody] RequestPhoneChangeRequest model, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var result = await userAccountService.RequestPhoneChangeAsync(
            userId, model.NewPhoneNumber, cancellationToken);
        if (result is null)
            return NotFound();
        return result.Succeeded
            ? Ok(new { Message = "验证码已发送" })
            : BadRequest(result.Errors);
    }

    [HttpPost("me/phone/confirm-change")]
    [EnableRateLimiting("user-sensitive")]
    public async Task<IActionResult> ConfirmPhoneChange(
        [FromBody] ConfirmPhoneChangeRequest model, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var result = await userAccountService.ConfirmPhoneChangeAsync(
            userId, model.Code, cancellationToken);
        if (result is null)
            return NotFound();
        return result.Succeeded
            ? Ok(new { Message = "手机号已验证，所有会话已失效，请重新登录" })
            : BadRequest(result.Errors);
    }

    [HttpPost("me/phone/cancel-change")]
    public async Task<IActionResult> CancelPhoneChange(CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var result = await userAccountService.CancelPhoneChangeAsync(userId, cancellationToken);
        if (result is null)
            return NotFound();
        return result.Succeeded ? Ok(new { Message = "已取消手机号变更" }) : BadRequest(result.Errors);
    }

    [HttpPost("me/email/confirm-change")]
    public async Task<IActionResult> ConfirmEmailChange(
        [FromBody] ConfirmEmailChangeRequest model, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var result = await userAccountService.ConfirmEmailChangeAsync(userId, model.Code, cancellationToken);
        if (result is null)
            return NotFound();

        return result.Succeeded
            ? Ok(new { Message = "邮箱已更新，请重新登录" })
            : BadRequest(result.Errors);
    }

    [HttpPost("me/email/cancel-change")]
    public async Task<IActionResult> CancelEmailChange(CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var result = await userAccountService.CancelEmailChangeAsync(userId, cancellationToken);
        if (result is null)
            return NotFound();

        return result.Succeeded ? Ok(new { Message = "已取消邮箱变更" }) : BadRequest(result.Errors);
    }

    [HttpPost("me/change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest model, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var sessionId = User.FindFirst(Core.Models.Auth.AuthClaimTypes.SessionId)?.Value;
        var result = await userAccountService.ChangePasswordWithSessionAsync(
            userId,
            model.CurrentPassword,
            model.NewPassword,
            model.RefreshToken,
            sessionId,
            cancellationToken);
        if (result is null)
            return NotFound();

        if (!result.Succeeded)
            return BadRequest(result.Errors);

        return Ok(new
        {
            Message = result.RequiresRelogin
                ? "密码已修改，请重新登录"
                : "密码已修改，当前会话已安全续签",
            RequiresRelogin = result.RequiresRelogin,
            Tokens = result.Tokens,
        });
    }

    [HttpGet("me/sessions")]
    public async Task<IActionResult> ListSessions(CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var sessions = await userAccountService.ListSessionsAsync(
            userId, deviceInfo.GetDeviceId(), cancellationToken);
        return Ok(sessions.Select(HttpContractMapper.ToHttpContract).ToArray());
    }

    [HttpDelete("me/sessions/{deviceId}")]
    public async Task<IActionResult> RevokeSession(string deviceId, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(new { Message = "deviceId 不能为空" });

        await userAccountService.RevokeSessionAsync(userId, deviceId, cancellationToken);
        return NoContent();
    }

    [HttpPost("me/sessions/revoke-others")]
    public async Task<IActionResult> RevokeOtherSessions(CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var currentDevice = deviceInfo.GetDeviceId();
        if (string.IsNullOrWhiteSpace(currentDevice))
            return BadRequest(new { Message = "缺少当前设备标识" });

        var count = await userAccountService.RevokeOtherSessionsAsync(userId, currentDevice, cancellationToken);
        return Ok(new RevokeSessionsResponse { Revoked = count });
    }

    [Authorize(Policy = ChatApp.Server.Authorization.AuthoritativeAdminAuthorization.PolicyName)]
    [HttpDelete("{userId:long}")]
    public async Task<IActionResult> DeleteUser(long userId, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var actorId))
            return Unauthorized();

        var result = await accountLifecycle.ScheduleDeletionByAdminAsync(
            userId,
            actorId,
            reason: "admin_delete_request",
            deviceInfo.GenerateDeviceInfo().IpAddress,
            cancellationToken);

        return result.Succeeded
            ? Accepted(new
            {
                Message = "用户已进入注销冷静期",
                ScheduledAfter = AuthTimingDefaults.AccountDeletionCooldown,
            })
            : BadRequest(result.Errors);
    }

    [Authorize(Policy = ChatApp.Server.Authorization.AuthoritativeAdminAuthorization.PolicyName)]
    [HttpGet("admin/disabled")]
    public async Task<IActionResult> ListDisabled(
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
        => Ok(await userAccountService.ListDisabledUsersAsync(cursor, limit, cancellationToken));

    [Authorize(Policy = ChatApp.Server.Authorization.AuthoritativeAdminAuthorization.PolicyName)]
    [HttpPost("{userId:long}/disable")]
    public async Task<IActionResult> DisableUser(
        long userId, [FromBody] AdminReasonRequest? body, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var actorId))
            return Unauthorized();

        var result = await userAccountService.DisableAsync(userId, body?.Reason, actorId, cancellationToken);
        if (result is null)
            return NotFound();

        return result.Succeeded ? Ok(new { Message = "用户已禁用" }) : BadRequest(result.Errors);
    }

    [Authorize(Policy = ChatApp.Server.Authorization.AuthoritativeAdminAuthorization.PolicyName)]
    [HttpPost("{userId:long}/enable")]
    public async Task<IActionResult> EnableUser(
        long userId, [FromBody] AdminReasonRequest? body, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var actorId))
            return Unauthorized();

        var result = await userAccountService.EnableAsync(userId, body?.Reason, actorId, cancellationToken);
        if (result is null)
            return NotFound();

        return result.Succeeded ? Ok(new { Message = "用户已启用" }) : BadRequest(result.Errors);
    }

    [Authorize(Policy = ChatApp.Server.Authorization.AuthoritativeAdminAuthorization.PolicyName)]
    [HttpPost("{userId:long}/force-logout")]
    public async Task<IActionResult> ForceLogout(
        long userId, [FromBody] AdminReasonRequest? body, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var actorId))
            return Unauthorized();

        var count = await userAccountService.ForceLogoutAsync(userId, body?.Reason, actorId, cancellationToken);
        return Ok(new { Revoked = count });
    }

    [Authorize(Policy = ChatApp.Server.Authorization.AuthoritativeAdminAuthorization.PolicyName)]
    [HttpDelete("{userId:long}/roles/{roleName}")]
    public async Task<IActionResult> RemoveRole(
        long userId, string roleName, [FromBody] RemoveRoleRequest? body, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var actorId))
            return Unauthorized();

        var result = await userAccountService.RemoveRoleAsync(
            userId, roleName, actorId, body?.Reason, body?.ConfirmSelfDemotion ?? false, cancellationToken);
        if (result is null) return NotFound();
        return result.Succeeded ? Ok(new { Message = "角色已移除，相关会话已失效" }) : BadRequest(result.Errors);
    }

    [Authorize(Policy = ChatApp.Server.Authorization.AuthoritativeAdminAuthorization.PolicyName)]
    [HttpPost("{userId:long}/roles")]
    public async Task<IActionResult> AssignRole(
        long userId, [FromBody] AssignRoleRequest body, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var actorId))
            return Unauthorized();

        var result = await userAccountService.AssignRoleAsync(
            userId, body.RoleName, actorId, body.Reason, cancellationToken);
        if (result is null) return NotFound();
        return result.Succeeded ? Ok(new { Message = "角色已分配，相关会话已失效" }) : BadRequest(result.Errors);
    }

    [Authorize(Policy = ChatApp.Server.Authorization.AuthoritativeAdminAuthorization.PolicyName)]
    [HttpGet("admin/audit-logs")]
    public async Task<IActionResult> QueryAuditLogs(
        [FromQuery] long? adminUserId = null,
        [FromQuery] long? targetUserId = null,
        [FromQuery] string? action = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
        => Ok(await adminAuditQuery.QueryAsync(
            adminUserId, targetUserId, action, from, to, cursor, limit, cancellationToken));
}
