using System.Security.Claims;
using ChatApp.Server.Controllers;
using ChatApp.Server.Models.Calls;
using ChatApp.Shared.Protocol.Tcp;
using Core.Interfaces;
using Core.Models.Common;
using Core.Models.Friend;
using Core.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Calls;

/// <summary>
/// GROUP-CALL-1：CallsController 群组 grant 签发（Mesh ≤4 人）单元测试。
/// <para>
/// 覆盖：群组签发 + 签名经 Shared 校验器 round-trip；逐参与者关系/拉黑校验（任一不合格 →
/// 整体拒绝且错误码指明成员）；名单非法（空/超上限/重复/含自己）拒绝；旧双人请求行为零变化。
/// </para>
/// </summary>
public sealed class CallsControllerGroupGrantTests
{
    private const string Secret = "calls-controller-group-test-secret";
    private const long CallerId = 1001;

    /// <summary>可编程关系 stub：默认全部互认好友且未拉黑。</summary>
    private sealed class StubFriendshipService : IFriendshipService
    {
        private readonly Dictionary<(long First, long Second), FriendshipStatusInfo> _relationships = [];

        public void SetRelationship(long userA, long userB, FriendshipStatusInfo info) =>
            _relationships[(Math.Min(userA, userB), Math.Max(userA, userB))] = info;

        public Task<FriendshipStatusInfo> CheckRelationshipAsync(long userId1, long userId2, CancellationToken ct = default) =>
            Task.FromResult(
                _relationships.TryGetValue((Math.Min(userId1, userId2), Math.Max(userId1, userId2)), out var info)
                    ? info
                    : new FriendshipStatusInfo { Status = FriendshipStatus.Approved, IsMutual = true });

        public Task<SendFriendRequestResult> SendRequestAsync(long requesterId, long targetUserId, string? message = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FriendshipOperationResult<FriendDto>> AcceptRequestAsync(long acceptorId, long requesterId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FriendshipOperationResult> DeclineRequestAsync(long declinerId, long requesterId, bool blockAfterDecline = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FriendshipOperationResult> WithdrawRequestAsync(long requesterId, long targetUserId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FriendshipOperationResult> BlockUserAsync(long blockerId, long targetUserId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FriendshipOperationResult> UnblockUserAsync(long unblockerId, long targetUserId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FriendshipOperationResult> DeleteFriendshipAsync(long userId, long friendId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CursorPage<FriendDto>> GetFriendsAsync(long userId, string? cursor = null, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CursorPage<FriendRequestDto>> GetRequestsAsync(long userId, FriendRequestType requestType, string? cursor = null, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<long, FriendshipStatusInfo>> CheckRelationshipsAsync(long watcherUserId, IReadOnlyList<long> targetUserIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<long, FriendshipStatusInfo>> CheckRelationshipsAuthoritativeAsync(long watcherUserId, IReadOnlyList<long> targetUserIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FriendshipOperationResult> UpdateFriendNoteAsync(long userId, long friendId, string note, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FriendshipOperationResult> AssignFriendToGroupAsync(long userId, long friendId, int groupId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FriendshipOperationResult<FriendGroupDto>> CreateGroupAsync(long userId, string groupName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FriendGroupDto>> ListGroupsAsync(long userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FriendshipOperationResult> RenameGroupAsync(long userId, int groupId, string newName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FriendshipOperationResult> ReorderGroupsAsync(long userId, IReadOnlyList<int> groupIdsInOrder, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FriendshipOperationResult> SetDefaultGroupAsync(long userId, int groupId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FriendshipOperationResult> DeleteGroupAsync(long userId, int groupId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CursorPage<FriendDto>> GetFriendsInGroupAsync(long userId, int groupId, string? cursor = null, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CursorPage<FriendSearchResultDto>> SearchFriendsAsync(long userId, string searchTerm, string? cursor = null, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CursorPage<BlockedUserDto>> GetBlockedUsersAsync(long userId, string? cursor = null, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static (CallsController Controller, StubFriendshipService Friendship) CreateController(
        string secret = Secret,
        int groupLifetimeSeconds = CallGrantOptions.DefaultGroupGrantLifetimeSeconds)
    {
        var friendship = new StubFriendshipService();
        var controller = new CallsController(
            friendship,
            Options.Create(new JwtSettings { Secret = secret }),
            Options.Create(new CallGrantOptions { GroupGrantLifetimeSeconds = groupLifetimeSeconds }));
        var user = new ClaimsPrincipal(
            new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, CallerId.ToString()),
            ], "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
        return (controller, friendship);
    }

    private static CallGrantRequest GroupRequest(params long[] invitees) => new()
    {
        CallKind = CallGrantContracts.CallKindGroup,
        ParticipantUserIds = invitees
    };

    private static CallGrantResponse ExtractGrant(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var dataType = ok.Value!.GetType().GetProperty("data")!;
        return Assert.IsType<CallGrantResponse>(dataType.GetValue(ok.Value));
    }

    private static string? ExtractError(IActionResult result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        return badRequest.Value!.GetType().GetProperty("error")?.GetValue(badRequest.Value) as string;
    }

    private static long? ExtractErrorUserId(IActionResult result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var raw = badRequest.Value!.GetType().GetProperty("userId")?.GetValue(badRequest.Value);
        return raw is null ? null : (long)raw;
    }

    [Fact]
    public async Task GroupGrant_HappyPath_SignatureCoversAllParticipants()
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateGrant(GroupRequest(2003, 2002), CancellationToken.None);

        var grant = ExtractGrant(result);
        Assert.Equal(CallGrantContracts.CallKindGroup, grant.CallKind);
        Assert.Equal(0, grant.CalleeUserId);
        Assert.Equal([CallerId, 2002, 2003], grant.ParticipantUserIds);

        // 签名经 Shared 权威校验器 round-trip（Gateway 群组中继同款校验）。
        var wire = CallGrantSigner.ToWireGrant(grant);
        Assert.True(TcpCallGrantSignature.TryVerify(wire, Secret, wire.ExpiresAtMs + 1, out var errorCode));
        Assert.Null(errorCode);
    }

    [Fact]
    public async Task GroupGrant_NotFriendsInvitee_RejectsWholeGrant_NamingMember()
    {
        var (controller, friendship) = CreateController();
        friendship.SetRelationship(CallerId, 2002, new FriendshipStatusInfo
        {
            Status = FriendshipStatus.Pending,
            IsMutual = false
        });

        var result = await controller.CreateGrant(GroupRequest(2002, 2003), CancellationToken.None);

        Assert.Equal(CallGrantErrorCode.NotFriends, ExtractError(result));
        Assert.Equal(2002, ExtractErrorUserId(result));
    }

    [Fact]
    public async Task GroupGrant_BlockedInvitee_RejectsWholeGrant_NamingMember()
    {
        var (controller, friendship) = CreateController();
        friendship.SetRelationship(CallerId, 2003, new FriendshipStatusInfo
        {
            Status = FriendshipStatus.Approved,
            IsMutual = true,
            IsBlocked = true
        });

        var result = await controller.CreateGrant(GroupRequest(2002, 2003), CancellationToken.None);

        Assert.Equal(CallGrantErrorCode.Blocked, ExtractError(result));
        Assert.Equal(2003, ExtractErrorUserId(result));
    }

    [Fact]
    public async Task GroupGrant_ExceedingMeshCap_Rejected()
    {
        var (controller, _) = CreateController();

        // 4 名被邀请人 + 主叫 = 5 > MaxGroupCallParticipants(4)。
        var result = await controller.CreateGrant(GroupRequest(2002, 2003, 2004, 2005), CancellationToken.None);

        Assert.Equal(CallGrantErrorCode.InvalidParticipants, ExtractError(result));
    }

    [Fact]
    public async Task GroupGrant_DuplicateOrSelfOrEmptyInvitees_Rejected()
    {
        var (controller, _) = CreateController();

        var duplicate = await controller.CreateGrant(GroupRequest(2002, 2002), CancellationToken.None);
        Assert.Equal(CallGrantErrorCode.InvalidParticipants, ExtractError(duplicate));

        var self = await controller.CreateGrant(GroupRequest(CallerId, 2002), CancellationToken.None);
        Assert.Equal(CallGrantErrorCode.InvalidParticipants, ExtractError(self));

        var empty = await controller.CreateGrant(GroupRequest(), CancellationToken.None);
        Assert.Equal(CallGrantErrorCode.InvalidParticipants, ExtractError(empty));
    }

    [Fact]
    public async Task ParticipantsWithoutGroupKind_Rejected()
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateGrant(new CallGrantRequest
        {
            CalleeUserId = 2002,
            ParticipantUserIds = [2002, 2003]
        }, CancellationToken.None);

        Assert.Equal(CallGrantErrorCode.InvalidParticipants, ExtractError(result));
    }

    [Fact]
    public async Task UnknownCallKind_Rejected()
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateGrant(new CallGrantRequest
        {
            CalleeUserId = 2002,
            CallKind = "mesh"
        }, CancellationToken.None);

        Assert.Equal(CallGrantErrorCode.InvalidCallKind, ExtractError(result));
    }

    [Fact]
    public async Task LegacyDirectGrant_BehaviorUnchanged()
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateGrant(new CallGrantRequest
        {
            CalleeUserId = 2002
        }, CancellationToken.None);

        var grant = ExtractGrant(result);
        Assert.Null(grant.CallKind);
        Assert.Null(grant.ParticipantUserIds);
        Assert.Equal(2002, grant.CalleeUserId);

        // Direct canonical 载荷与 0.5.6 双人格式逐字节一致，签名可被 Shared 校验器（Direct 规则）验证。
        var wire = CallGrantSigner.ToWireGrant(grant);
        Assert.Equal(
            $"{grant.CallId}|{CallerId}|2002|{grant.ExpiresAtMs}|{grant.Nonce}",
            TcpCallGrantSignature.BuildCanonicalPayload(wire));
        Assert.True(TcpCallGrantSignature.TryVerify(wire, Secret, wire.ExpiresAtMs + 1, out _));
    }

    [Fact]
    public async Task MissingSigningSecret_Returns503_AlsoForGroup()
    {
        var (controller, _) = CreateController(secret: " ");

        var result = await controller.CreateGrant(GroupRequest(2002), CancellationToken.None);

        var statusCode = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusCode.StatusCode);
    }

    // ---- GROUP-CALL-MIDJOIN-1：群组重签（同 CallId 换发新批次）与可配 TTL ----

    [Fact]
    public async Task GroupGrant_ResignWithExplicitCallId_ReusesCallId_UpdatesParticipants()
    {
        var (controller, _) = CreateController();
        const string existingCallId = "0123456789abcdef0123456789abcdef";

        var request = GroupRequest(2002, 2003, 2004);
        request.CallId = existingCallId;
        var result = await controller.CreateGrant(request, CancellationToken.None);

        var grant = ExtractGrant(result);
        // 同 CallId 重签：原样采用（中期加人不迁移房间）。
        Assert.Equal(existingCallId, grant.CallId);
        Assert.Equal([CallerId, 2002, 2003, 2004], grant.ParticipantUserIds);
        Assert.Equal(CallGrantContracts.CallKindGroup, grant.CallKind);

        // 重签的签名经 Shared 校验器 round-trip（HMAC 覆盖重签后的 callId 与新名单）。
        var wire = CallGrantSigner.ToWireGrant(grant);
        Assert.True(TcpCallGrantSignature.TryVerify(wire, Secret, wire.ExpiresAtMs + 1, out var errorCode));
        Assert.Null(errorCode);

        // canonical 载荷确实覆盖重签 callId（篡改 callId 即签名失效）。
        Assert.Contains(existingCallId, TcpCallGrantSignature.BuildCanonicalPayload(wire));
        var tampered = CallGrantSigner.ToWireGrant(grant);
        tampered.CallId = new string('f', 32);
        Assert.False(TcpCallGrantSignature.TryVerify(tampered, Secret, wire.ExpiresAtMs, out _));
    }

    [Fact]
    public async Task GroupGrant_ResignIssuesNewNonce_AndExpiresLaterThanDirect()
    {
        var (controller, _) = CreateController();
        const string existingCallId = "resign-call-1";

        var first = ExtractGrant(await controller.CreateGrant(
            GroupRequestWithCallId(existingCallId, 2002), CancellationToken.None));
        var second = ExtractGrant(await controller.CreateGrant(
            GroupRequestWithCallId(existingCallId, 2002, 2003), CancellationToken.None));

        // 同 CallId 两次重签：nonce 每次新发（防重放语义不变），名单随重签更新。
        Assert.Equal(existingCallId, first.CallId);
        Assert.Equal(existingCallId, second.CallId);
        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.Equal([CallerId, 2002], first.ParticipantUserIds);
        Assert.Equal([CallerId, 2002, 2003], second.ParticipantUserIds);
    }

    [Fact]
    public async Task GroupGrant_DefaultTtlIsFourHours_DirectStaysSixtySeconds()
    {
        var (controller, _) = CreateController();
        var beforeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var group = ExtractGrant(await controller.CreateGrant(GroupRequest(2002), CancellationToken.None));
        var direct = ExtractGrant(await controller.CreateGrant(
            new CallGrantRequest { CalleeUserId = 2002 }, CancellationToken.None));

        // 群组：缺省 4 小时；双人：恒为 60s（既有语义零改动）。
        AssertInLifetime(group.ExpiresAtMs - beforeMs, CallGrantOptions.DefaultGroupGrantLifetimeSeconds);
        AssertInLifetime(direct.ExpiresAtMs - beforeMs, CallGrantContracts.MaxGrantLifetimeSeconds);
    }

    [Fact]
    public async Task GroupGrant_ConfigurableTtl_IsHonored()
    {
        var (controller, _) = CreateController(groupLifetimeSeconds: 7200);
        var beforeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var grant = ExtractGrant(await controller.CreateGrant(GroupRequest(2002), CancellationToken.None));

        AssertInLifetime(grant.ExpiresAtMs - beforeMs, 7200);
    }

    [Fact]
    public async Task DirectGrant_IgnoresClientCallId_AlwaysIssuesNewCallId()
    {
        var (controller, _) = CreateController();

        var request = new CallGrantRequest { CalleeUserId = 2002, CallId = "should-be-ignored" };
        var grant = ExtractGrant(await controller.CreateGrant(request, CancellationToken.None));

        // 双人通话不重签：恒为新 CallId、60s 有效期、wire 形态不变（CallKind/名单缺省）。
        Assert.NotEqual("should-be-ignored", grant.CallId);
        Assert.Null(grant.CallKind);
        Assert.Null(grant.ParticipantUserIds);
        var beforeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        AssertInLifetime(grant.ExpiresAtMs - beforeMs, CallGrantContracts.MaxGrantLifetimeSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad id with spaces")]
    [InlineData("pipe|injection")]
    [InlineData("非ASCII字符")]
    public async Task GroupGrant_MalformedResignCallId_RejectedWithStableCode(string badCallId)
    {
        var (controller, _) = CreateController();

        var request = GroupRequest(2002);
        request.CallId = badCallId;
        var result = await controller.CreateGrant(request, CancellationToken.None);

        Assert.Equal(CallGrantErrorCode.InvalidCallId, ExtractError(result));
    }

    [Fact]
    public async Task GroupGrant_OverlongResignCallId_Rejected()
    {
        var (controller, _) = CreateController();

        var request = GroupRequest(2002);
        request.CallId = new string('a', TcpCallConstants.MaxCallIdBytes + 1);
        var result = await controller.CreateGrant(request, CancellationToken.None);

        Assert.Equal(CallGrantErrorCode.InvalidCallId, ExtractError(result));
    }

    private static CallGrantRequest GroupRequestWithCallId(string callId, params long[] invitees)
    {
        var request = GroupRequest(invitees);
        request.CallId = callId;
        return request;
    }

    private static void AssertInLifetime(long actualLifetimeMs, int expectedSeconds)
    {
        // 容忍时钟推进：预期寿命与实际差值落在 [0, 2s) 窗口内。
        Assert.InRange(actualLifetimeMs, (expectedSeconds - 2) * 1000L, expectedSeconds * 1000L + 100);
    }
}
