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
/// 1:1 语音通话的短期 call grant 签发端点（CALL-E2E-2）。
/// <para>
/// Server 作为授权权威签发短期 grant：校验主被叫为互认好友且无屏蔽，再以
/// <c>JwtSettings.Secret</c> 对规范载荷做 HMAC-SHA256 签名。Realtime 侧
/// <c>SignedCallGrantVerifier</c> 用同一密钥校验，驱动通话信令状态机。
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
    /// 为当前登录用户与被叫用户签发短期 call grant。
    /// </summary>
    [HttpPost("grants")]
    [EnableRateLimiting("friendship-write")]
    public async Task<IActionResult> CreateGrant(
        [FromBody] CallGrantRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var callerUserId))
            return Unauthorized(new { error = CallGrantErrorCode.InvalidTargetUser });

        if (request.CalleeUserId <= 0 || request.CalleeUserId == callerUserId)
            return BadRequest(new { error = CallGrantErrorCode.InvalidTargetUser });

        var relationship = await friendshipService.CheckRelationshipAsync(
            callerUserId, request.CalleeUserId, cancellationToken).ConfigureAwait(false);

        // 屏蔽优先拒绝（任一方向存在 BlockRecord）。
        if (relationship.IsBlocked)
            return BadRequest(new { error = CallGrantErrorCode.Blocked });

        // 仅互认好友（Approved + IsMutual）可发起 1:1 通话。
        if (relationship.Status != FriendshipStatus.Approved || !relationship.IsMutual)
            return BadRequest(new { error = CallGrantErrorCode.NotFriends });

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

        var grant = new CallGrantResponse
        {
            CallId = callId,
            CallerUserId = callerUserId,
            CalleeUserId = request.CalleeUserId,
            ExpiresAtMs = nowMs + CallGrantContracts.MaxGrantLifetimeSeconds * 1000L,
            Nonce = nonce,
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