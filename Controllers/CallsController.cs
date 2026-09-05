using System.Security.Claims;
using ChatApp.Server.Models.Calls;
using ChatApp.Shared.Protocol.Tcp;
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
/// <para>
/// 群组重签（GROUP-CALL-MIDJOIN-1）：请求可选 <c>callId</c> 存在且格式合法时原样采用
/// （同 CallId 换发新批次，更新参与者名单——中期加人不再迁移房间）；缺省则新生成。
/// 双人 grant 有效期恒为 60s；群组 grant 有效期由 <see cref="CallGrantOptions"/> 配置
/// （缺省 4 小时），使群组会话在整场通话内可持续换发新批次。
/// </para>
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CallsController(
    IFriendshipService friendshipService,
    IOptions<CoreSettings> jwtOptions,
    IOptions<CallGrantOptions> grantOptions) : ControllerBase
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

        // 群组重签（GROUP-CALL-MIDJOIN-1）：可选 callId 存在且格式合法 → 原样采用
        // （同 CallId 换发新批次，更新参与者名单）；缺省（null）→ 新生成；显式空白/非法
        // 形态 fail-closed（存在但格式不合法）。
        var requestedCallId = request.CallId?.Trim();
        if (isGroupCall
            && request.CallId is not null
            && (requestedCallId is null || !IsValidCallId(requestedCallId)))
        {
            return BadRequest(new { error = CallGrantErrorCode.InvalidCallId });
        }

        var secret = jwtOptions.Value.Secret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            // fail-closed：未配置签名密钥时拒绝签发，不猜测。
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = CallGrantErrorCode.SigningUnavailable });
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var callId = isGroupCall
            ? (string.IsNullOrEmpty(requestedCallId) ? Guid.NewGuid().ToString("N") : requestedCallId)
            : Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");

        // 有效期：双人恒为 60s（既有语义零改动）；群组可配（缺省 4 小时）——无状态中继按
        // grant 过期时间放行成员信令，整场群组通话可持续换发新批次（End/重连/中途加人）。
        var lifetimeSeconds = isGroupCall
            ? grantOptions.Value.GroupGrantLifetimeSeconds
            : CallGrantContracts.MaxGrantLifetimeSeconds;

        // 群组：成员集合 = grant 签发名单（含主叫、升序）；CalleeUserId=0 使群组 grant 在
        // 旧双人校验端 fail-closed，绝不误入 1:1 状态机。
        var grant = new CallGrantResponse
        {
            CallId = callId,
            CallerUserId = callerUserId,
            CalleeUserId = isGroupCall ? 0 : request.CalleeUserId,
            ExpiresAtMs = nowMs + lifetimeSeconds * 1000L,
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

    /// <summary>
    /// 群组重签 callId 格式校验：非空白、UTF-8 ≤ <see cref="TcpCallConstants.MaxCallIdBytes"/> 字节
    /// （与 Gateway 命令校验一致）、且仅含受限字符集（<see cref="CallGrantContracts.ValidCallIdChars"/>，
    /// 不含 canonical 载荷分隔符 <c>|</c>，防签名载荷注入）。
    /// </summary>
    private static bool IsValidCallId(string callId)
        => callId.Length > 0
           && callId.Length <= TcpCallConstants.MaxCallIdBytes
           && System.Text.Encoding.UTF8.GetByteCount(callId) <= TcpCallConstants.MaxCallIdBytes
           && callId.All(CallGrantContracts.ValidCallIdChars.Contains);

    private bool TryGetCurrentUserId(out long userId)
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? User.FindFirstValue(ClaimTypes.Name)
                  ?? User.FindFirstValue("sub");
        return long.TryParse(raw, out userId);
    }
}