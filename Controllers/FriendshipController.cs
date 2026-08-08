using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ChatApp.Contracts.Http.Common;
using ChatApp.Server.Models;
using Core.Interfaces;
using Core.Models.Friend;
using Core.Models.Friend.Requests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using HttpBlockedUserDto = ChatApp.Contracts.Http.Friends.BlockedUserDto;
using HttpFriendDto = ChatApp.Contracts.Http.Friends.FriendDto;
using HttpFriendRequestDto = ChatApp.Contracts.Http.Friends.FriendRequestDto;
using SendFriendRequestRequest = ChatApp.Contracts.Http.Friends.SendFriendRequestRequest;

namespace ChatApp.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FriendshipController(
    IFriendshipService friendshipService) : ControllerBase
{
    /// <summary>
    /// 好友列表
    /// </summary>
    /// <returns></returns>
    [HttpGet("all")]
    public async Task<CursorPage<HttpFriendDto>> GetAllFriends(
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var page = await friendshipService.GetFriendsAsync(
            GetCurrentUserId(),
            cursor,
            limit,
            cancellationToken);
        return page.ToHttpContract();
    }

    /// <summary>
    /// 删除指定的好友。
    /// </summary>
    /// <param name="friendId">要删除的好友ID。</param>
    /// <returns>表示异步操作的结果，成功时返回200 OK，失败时返回400 Bad Request。</returns>
    [HttpDelete("{friendId:long}")]
    [EnableRateLimiting("friendship-write")]
    public async Task<IActionResult> DeleteFriend([FromRoute, Range(1, long.MaxValue)] long friendId, CancellationToken cancellationToken)
    {
        var result = await friendshipService.DeleteFriendshipAsync(
            GetCurrentUserId(), friendId, cancellationToken);
        return HandleServiceResult(result);
    }

    /// <summary>
    /// 发送好友请求
    /// </summary>
    /// <param name="request">包含目标用户ID和可选消息的请求对象</param>
    /// <returns>表示请求发送结果的操作结果</returns>
    [HttpPost("requests")]
    [EnableRateLimiting("friendship-write")]
    [Filters.Idempotent]
    public async Task<IActionResult> SendFriendRequest(
        [FromBody] SendFriendRequestRequest request,
        CancellationToken cancellationToken)
    {
        var result = await friendshipService.SendRequestAsync(
            GetCurrentUserId(), request.TargetUserId, request.Message, cancellationToken);
        return HandleServiceResult(result);
    }

    /// <summary>
    /// 接受来自指定用户的好友请求。
    /// </summary>
    /// <param name="requesterId">请求方用户的ID。</param>
    /// <returns>返回一个表示操作结果的IActionResult对象，成功时包含新添加的好友信息。</returns>
    [HttpPut("requests/{requesterId:long}/accept")]
    [EnableRateLimiting("friendship-write")]
    public async Task<IActionResult> AcceptFriendRequest([FromRoute, Range(1, long.MaxValue)] long requesterId, CancellationToken cancellationToken)
    {
        var result = await friendshipService.AcceptRequestAsync(
            GetCurrentUserId(), requesterId, cancellationToken);
        return HandleServiceResult(result);
    }

    /// <summary>
    /// 拒绝好友请求。
    /// </summary>
    /// <param name="requesterId">请求方用户ID。</param>
    /// <param name="blockAfterDecline">拒绝后是否拉黑请求方，默认为false。</param>
    /// <returns>操作结果，成功返回200 OK，失败返回400 Bad Request。</returns>
    [HttpPut("requests/{requesterId:long}/decline")]
    [EnableRateLimiting("friendship-write")]
    public async Task<IActionResult> DeclineFriendRequest(
        [FromRoute, Range(1, long.MaxValue)] long requesterId,
        [FromQuery] bool blockAfterDecline = false,
        CancellationToken cancellationToken = default)
    {
        var result = await friendshipService.DeclineRequestAsync(
            GetCurrentUserId(), requesterId, blockAfterDecline, cancellationToken);
        return HandleServiceResult(result);
    }

    /// <summary>撤回自己发出的待处理好友申请。</summary>
    [HttpDelete("requests/{targetUserId:long}")]
    [EnableRateLimiting("friendship-write")]
    public async Task<IActionResult> WithdrawFriendRequest(
        [FromRoute, Range(1, long.MaxValue)] long targetUserId, CancellationToken cancellationToken)
    {
        var result = await friendshipService.WithdrawRequestAsync(
            GetCurrentUserId(), targetUserId, cancellationToken);
        return HandleServiceResult(result);
    }

    /// <summary>
    /// 获取当前用户的所有待处理的入站好友请求。
    /// </summary>
    /// <returns>一个包含所有入站好友请求的异步可枚举集合。</returns>
    [HttpGet("requests/incoming")] // ← 拆成两个端点
    public async Task<CursorPage<HttpFriendRequestDto>> GetIncomingRequests(
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var page = await friendshipService.GetRequestsAsync(
            GetCurrentUserId(),
            FriendRequestType.Incoming,
            cursor,
            limit,
            cancellationToken);
        return page.ToHttpContract();
    }

    /// <summary>
    /// 获取当前用户发出的好友请求列表。
    /// </summary>
    /// <returns>一个包含好友请求信息的异步可枚举集合。</returns>
    [HttpGet("requests/outgoing")]
    public async Task<CursorPage<HttpFriendRequestDto>> GetOutgoingRequests(
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var page = await friendshipService.GetRequestsAsync(
            GetCurrentUserId(),
            FriendRequestType.Outgoing,
            cursor,
            limit,
            cancellationToken);
        return page.ToHttpContract();
    }


    /// <summary>
    /// 拉黑指定用户
    /// </summary>
    /// <param name="request">包含目标用户ID的请求对象</param>
    /// <returns>返回操作结果，成功时为Ok响应，失败时为BadRequest响应</returns>
    [HttpPost("block")]
    [EnableRateLimiting("friendship-write")]
    public async Task<IActionResult> BlockUser([FromBody] BlockUserRequest request, CancellationToken cancellationToken)
    {
        var result = await friendshipService.BlockUserAsync(
            GetCurrentUserId(), request.TargetUserId, cancellationToken);
        return HandleServiceResult(result);
    }

    /// <summary>
    /// 解除对指定用户的拉黑状态。
    /// </summary>
    /// <param name="targetUserId">要解除拉黑的目标用户ID。</param>
    /// <returns>返回一个表示操作结果的IActionResult对象，成功时包含解封信息，失败时包含错误详情。</returns>
    [HttpDelete("block/{targetUserId:long}")]
    [EnableRateLimiting("friendship-write")]
    public async Task<IActionResult> UnblockUser([FromRoute, Range(1, long.MaxValue)] long targetUserId, CancellationToken cancellationToken)
    {
        var result = await friendshipService.UnblockUserAsync(
            GetCurrentUserId(), targetUserId, cancellationToken);
        return HandleServiceResult(result);
    }

    /// <summary>
    /// 获取当前用户已屏蔽的用户列表。
    /// </summary>
    /// <returns>一个包含被屏蔽用户信息的异步可枚举集合。</returns>
    [HttpGet("blocked")]
    public async Task<CursorPage<HttpBlockedUserDto>> GetBlockedUsers(
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var page = await friendshipService.GetBlockedUsersAsync(
            GetCurrentUserId(),
            cursor,
            limit,
            cancellationToken);
        return page.ToHttpContract();
    }

    /// <summary>
    /// 更新指定好友的备注信息。
    /// </summary>
    /// <param name="friendId">要更新备注的好友ID。</param>
    /// <param name="request">包含新备注信息的请求对象。</param>
    /// <returns>如果操作成功，返回200状态码；如果失败，返回400状态码及错误详情。</returns>
    [HttpPut("friends/{friendId:long}/note")]
    [EnableRateLimiting("friendship-write")]
    public async Task<IActionResult> UpdateFriendNote(
        [FromRoute, Range(1, long.MaxValue)] long friendId, [FromBody] UpdateNoteRequest request, CancellationToken cancellationToken)
    {
        var result = await friendshipService.UpdateFriendNoteAsync(
            GetCurrentUserId(), friendId, request.Note, cancellationToken);
        return HandleServiceResult(result);
    }

    /// <summary>
    /// 将好友分配到指定的分组
    /// </summary>
    /// <param name="friendId">要分配的好友ID</param>
    /// <param name="request">包含目标分组ID的请求对象</param>
    /// <returns>返回操作结果，成功时返回200 OK，失败时返回400 Bad Request</returns>
    [HttpPut("friends/{friendId:long}/group")]
    [EnableRateLimiting("friendship-write")]
    public async Task<IActionResult> AssignFriendToGroup(
        [FromRoute, Range(1, long.MaxValue)] long friendId, [FromBody] AssignGroupRequest request, CancellationToken cancellationToken)
    {
        var result = await friendshipService.AssignFriendToGroupAsync(
            GetCurrentUserId(), friendId, request.GroupId, cancellationToken);
        return HandleServiceResult(result);
    }

    [HttpPost("groups")]
    [EnableRateLimiting("friendship-write")]
    public async Task<IActionResult> CreateGroup(
        [FromBody] CreateFriendGroupRequest request, CancellationToken cancellationToken)
    {
        var result = await friendshipService.CreateGroupAsync(
            GetCurrentUserId(), request.GroupName, cancellationToken);
        return HandleServiceResult(result);
    }

    [HttpGet("groups")]
    public async Task<IActionResult> ListGroups(CancellationToken cancellationToken)
    {
        var groups = await friendshipService.ListGroupsAsync(GetCurrentUserId(), cancellationToken);
        return Ok(groups);
    }

    [HttpPut("groups/reorder")]
    [EnableRateLimiting("friendship-write")]
    public async Task<IActionResult> ReorderGroups(
        [FromBody] ReorderFriendGroupsRequest request, CancellationToken cancellationToken)
    {
        var result = await friendshipService.ReorderGroupsAsync(
            GetCurrentUserId(), request.GroupIdsInOrder, cancellationToken);
        return HandleServiceResult(result);
    }

    [HttpPut("groups/default")]
    [EnableRateLimiting("friendship-write")]
    public async Task<IActionResult> SetDefaultGroup(
        [FromBody] SetDefaultFriendGroupRequest request, CancellationToken cancellationToken)
    {
        var result = await friendshipService.SetDefaultGroupAsync(
            GetCurrentUserId(), request.GroupId, cancellationToken);
        return HandleServiceResult(result);
    }

    [HttpPut("groups/{groupId:int}")]
    [EnableRateLimiting("friendship-write")]
    public async Task<IActionResult> RenameGroup(
        [FromRoute] int groupId, [FromBody] RenameFriendGroupRequest request, CancellationToken cancellationToken)
    {
        var result = await friendshipService.RenameGroupAsync(
            GetCurrentUserId(), groupId, request.GroupName, cancellationToken);
        return HandleServiceResult(result);
    }

    [HttpDelete("groups/{groupId:int}")]
    [EnableRateLimiting("friendship-write")]
    public async Task<IActionResult> DeleteGroup([FromRoute] int groupId, CancellationToken cancellationToken)
    {
        var result = await friendshipService.DeleteGroupAsync(GetCurrentUserId(), groupId, cancellationToken);
        return HandleServiceResult(result);
    }

    [HttpGet("groups/{groupId:int}/friends")]
    public async Task<CursorPage<HttpFriendDto>> GetFriendsInGroup(
        [FromRoute] int groupId,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var page = await friendshipService.GetFriendsInGroupAsync(
            GetCurrentUserId(), groupId, cursor, limit, cancellationToken);
        return page.ToHttpContract();
    }

    /// <summary>
    /// 搜索好友（支持名称/备注搜索）
    /// </summary>
    /// <param name="searchTerm">用于搜索的好友名称或备注关键字</param>
    /// <returns>符合条件的好友搜索结果列表</returns>
    [HttpGet("search")]
    public async Task<CursorPage<FriendSearchResultDto>> SearchFriends(
        [FromQuery] string searchTerm,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var page = await friendshipService.SearchFriendsAsync(
            GetCurrentUserId(), searchTerm, cursor, limit, cancellationToken);
        return page.ToHttpContract();
    }

    #region 辅助方法
    private long GetCurrentUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(claim, out var id)
            ? id
            : throw new UnauthorizedAccessException("无效的用户凭证");
    }


   
    private IActionResult HandleServiceResult(FriendshipOperationResult result)
    {
        var response = result.ToHttpContract();
        return result.IsSuccess
            ? Ok(new ApiEnvelope<ChatApp.Contracts.Http.Friends.FriendshipOperationResponse>
            {
                Data = response,
            })
            : BadRequest(response);
    }

    private IActionResult HandleServiceResult(SendFriendRequestResult result)
    {
        var response = result.ToHttpContract();
        return result.IsSuccess
            ? Ok(new ApiEnvelope<ChatApp.Contracts.Http.Friends.SendFriendRequestResponse>
            {
                Data = response,
            })
            : BadRequest(response);
    }

    private IActionResult HandleServiceResult(FriendshipOperationResult<FriendDto> result)
    {
        var response = result.ToHttpContract();
        return result.Succeeded
            ? Ok(new ApiEnvelope<HttpFriendDto> { Data = response.Data })
            : BadRequest(response);
    }

    private IActionResult HandleServiceResult<T>(FriendshipOperationResult<T> result)
    {
        var response = result.ToHttpContract();
        return result.Succeeded
            ? Ok(new ApiEnvelope<T> { Data = response.Data })
            : BadRequest(response);
    }
    #endregion
}
