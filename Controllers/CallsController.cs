using System.Security.Claims;
using ChatApp.Server.Models.Calls;
using Core.Interfaces;
using Core.Models.Friend;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using CoreSettings = Core.Settings.JwtSettings;

namespace ChatApp.Server.Controllers;

/// <summary>
/// 语音通话的短期 call grant 签发端点（CALL-E2E-2；GROUP-CALL-1 多人化）。
/// <para>
/// Server 作为授权权威签发短期 grant：双人通话校验主被叫为互认好友且无屏蔽；群组通话
/// （Mesh ≤4 人）逐参与者校验关系/拉黑（任一不合格 → 整体拒绝并指明成员），再以
/// <c>JwtSettings.Secret</c> 对规范载荷做 HMAC-SHA256 签名——群组签名覆盖全部参与者。
/// Gateway 群组中继 / Realtime <c>SignedCallGrantVerifier</c> 用同一密钥校验。
/// </para>
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CallsController(
    IFriendshipService friendshipService,
    IOptions<CoreSettings> jwtOptions) : ControllerBase
{
    /// <summary>
    /// 为当前登录用户签发短期 call grant（双人被叫 = calleeUserId；群组 = callKind="group" +
    /// participantUserIds 被邀请人列表，不含主叫）。
    /// </summary>
    [HttpPost("grants")]
    [EnableRateLimiting("friendship-write")]
    public async Task<IActionResult> CreateGrant(
        [FromBody] CallGrantRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var callerUserId))
            return Unauthorized(new { error = CallGrantErrorCode.InvalidTargetUser });

        var isGroupCall = !string.IsNullOrWhiteSpace(request.CallKind);
        if (isGroupCall
            && !string.Equals(request.CallKind, CallGrantContracts.CallKindGroup, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = CallGrantErrorCode.InvalidCallKind });
        }

        // 双人通话（旧语义）：任何参与者列表组合都拒绝，防止静默降级解释。
        if (!isGroupCall && request.ParticipantUserIds is { Count: > 0 })
            return BadRequest(new { error = CallGrantErrorCode.InvalidParticipants });

        IReadOnlyList<long> invitees = Array.Empty<long>();
        if (isGroupCall)
        {
            var candidateInvitees = request.ParticipantUserIds ?? Array.Empty<long>();
            if (candidateInvitees.Count == 0
                || candidateInvitees.Count + 1 > CallGrantContracts.MaxGroupCallParticipants
                || candidateInvitees.Any(id => id <= 0)
                || candidateInvitees.Any(id => id == callerUserId)
                || candidateInvitees.Distinct().Count() != candidateInvitees.Count)
            {
                return BadRequest(new { error = CallGrantErrorCode.InvalidParticipants });
            }

            // 逐参与者关系/拉黑校验：任一不合格 → 整体拒绝（错误码指明成员）。
            foreach (var invitee in candidateInvitees)
            {
                var inviteeRelationship = await friendshipService.CheckRelationshipAsync(
                    callerUserId, invitee, cancellationToken).ConfigureAwait(false);

                if (inviteeRelationship.IsBlocked)
                    return BadRequest(new { error = CallGrantErrorCode.Blocked, userId = invitee });

                if (inviteeRelationship.Status != FriendshipStatus.Approved || !inviteeRelationship.IsMutual)
                    return BadRequest(new { error = CallGrantErrorCode.NotFriends, userId = invitee });
            }

            invitees = candidateInvitees;
        }
        else
        {
            if ((!isGroupCall && request.CalleeUserId <= 0) || request.CalleeUserId == callerUserId)
                return BadRequest(new { error = CallGrantErrorCode.InvalidTargetUser });

            var relationship = await friendshipService.CheckRelationshipAsync(
                callerUserId, request.CalleeUserId, cancellationToken).ConfigureAwait(false);

            // 屏蔽优先拒绝（任一方向存在 BlockRecord）。
            if (relationship.IsBlocked)
                return BadRequest(new { error = CallGrantErrorCode.Blocked });

            // 仅互认好友（Approved + IsMutual）可发起 1:1 通话。
            if (relationship.Status != FriendshipStatus.Approved || !relationship.IsMutual)
                return BadRequest(new { error = CallGrantErrorCode.NotFriends });
        }

        var secret = jwtOptions.Value.Secret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            // fail-closed：未配置签名密钥时拒绝签发，不猜测。
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = CallGrantErrorCode.SigningUnavailable });
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var callId = Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");

        // 群组：成员集合 = grant 签发名单（含主叫、升序）；CalleeUserId=0 使群组 grant 在
        // 旧双人校验端 fail-closed，绝不误入 1:1 状态机。
        var grant = new CallGrantResponse
        {
            CallId = callId,
            CallerUserId = callerUserId,
            CalleeUserId = isGroupCall ? 0 : request.CalleeUserId,
            ExpiresAtMs = nowMs + CallGrantContracts.MaxGrantLifetimeSeconds * 1000L,
            Nonce = nonce,
            CallKind = isGroupCall ? CallGrantContracts.CallKindGroup : null,
            ParticipantUserIds = isGroupCall
                ? invitees.Prepend(callerUserId).OrderBy(id => id).ToArray()
                : null,
            Signature = string.Empty
        };
        grant.Signature = CallGrantSigner.Sign(secret, grant);

        return Ok(new { data = grant });
    }

    private bool TryGetCurrentUserId(out long userId)
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? User.FindFirstValue(ClaimTypes.Name)
                  ?? User.FindFirstValue("sub");
        return long.TryParse(raw, out userId);
    }
}