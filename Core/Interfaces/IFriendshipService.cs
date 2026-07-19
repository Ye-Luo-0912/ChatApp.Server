using System.Linq.Expressions;
using Core.Models.Common;
using Core.Models.Friend;

namespace Core.Interfaces;

public interface IFriendshipService
{
    /// <summary>
    /// 发送好友请求
    /// </summary>
    /// <param name="requesterId">发送请求的用户ID</param>
    /// <param name="targetUserId">目标用户的ID</param>
    /// <param name="message">可选的消息，随请求发送给目标用户</param>
    /// <param name="ct">取消令牌，用于支持操作的取消</param>
    /// <returns>表示请求发送结果的枚举值</returns>
    Task<SendFriendRequestResult> SendRequestAsync(long requesterId, long targetUserId, string? message = null,
        CancellationToken ct = default);

    /// <summary>
    /// 接受好友请求
    /// </summary>
    /// <param name="acceptorId">接受者用户ID</param>
    /// <param name="requesterId">请求方用户ID</param>
    /// <param name="ct">取消令牌，用于支持操作的取消</param>
    /// <returns>包含操作结果和新添加的好友信息的数据对象</returns>
    Task<FriendshipOperationResult<FriendDto>> AcceptRequestAsync(long acceptorId, long requesterId,
        CancellationToken ct = default);

    /// <summary>
    /// 拒绝好友请求
    /// </summary>
    /// <param name="declinerId">拒绝方用户ID</param>
    /// <param name="requesterId">请求方用户ID</param>
    /// <param name="blockAfterDecline">拒绝后是否拉黑</param>
    /// <param name="ct">取消令牌，用于支持操作的取消</param>
    Task<FriendshipOperationResult> DeclineRequestAsync(long declinerId, long requesterId,
        bool blockAfterDecline = false, CancellationToken ct = default);

    /// <summary>
    /// 拉黑用户
    /// </summary>
    /// <param name="blockerId">拉黑者ID</param>
    /// <param name="targetUserId">目标用户的ID</param>
    /// <param name="ct">取消令牌，用于支持操作的取消</param>
    Task<FriendshipOperationResult> BlockUserAsync(long blockerId, long targetUserId, CancellationToken ct = default);

    /// <summary>
    /// 解除对指定用户的封禁
    /// </summary>
    /// <param name="unblockerId">执行解封操作的用户ID</param>
    /// <param name="targetUserId">被解封的目标用户ID</param>
    /// <param name="ct">取消令牌，用于支持操作的取消</param>
    /// <returns>表示解封操作结果的对象</returns>
    Task<FriendshipOperationResult> UnblockUserAsync(long unblockerId, long targetUserId,
        CancellationToken ct = default);

    /// <summary>
    /// 删除好友关系
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="friendId">好友ID</param>
    /// <param name="ct">取消令牌，用于支持操作的取消</param>
    Task<FriendshipOperationResult> DeleteFriendshipAsync(long userId, long friendId, CancellationToken ct = default);

    /// <summary>
    /// 获取好友列表（游标分页）
    /// </summary>
    /// <param name="userId">当前用户ID</param>
    /// <param name="func"></param>
    /// <param name="cursor">游标，首次请求传 null</param>
    /// <param name="limit">每页数量，默认 50，最大 100</param>
    /// <param name="ct"></param>
    Task<CursorPage<T>> GetFriendsAsync<T>(long userId, Expression<Func<UserFriendEntry, T>> func,
        string? cursor = null, int limit = 50, CancellationToken ct = default) where T : class;

    /// <summary>
    /// 获取好友请求列表（游标分页）
    /// </summary>
    /// <param name="userId">当前用户ID</param>
    /// <param name="func"></param>
    /// <param name="requestType">请求类型（收到的/发出的）</param>
    /// <param name="cursor">游标，首次请求传 null</param>
    /// <param name="limit">每页数量，默认 50，最大 100</param>
    /// <param name="ct"></param>
    Task<CursorPage<T>> GetRequestsAsync<T>(long userId, Expression<Func<FriendRequest, T>> func,
        FriendRequestType requestType, string? cursor = null, int limit = 50,
        CancellationToken ct = default) where T : class;

    /// <summary>
    /// 检查两人关系状态
    /// </summary>
    Task<FriendshipStatusInfo> CheckRelationshipAsync(long userId1, long userId2,CancellationToken ct = default);

    /// <summary>
    /// 更新好友备注
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="friendId">好友ID</param>
    /// <param name="note">备注</param>
    /// <param name="ct">取消令牌，用于支持操作的取消</param>
    Task<FriendshipOperationResult> UpdateFriendNoteAsync(long userId, long friendId, string note, CancellationToken ct = default);

    /// <summary>
    /// 设置好友分组
    /// </summary>
    Task<FriendshipOperationResult> AssignFriendToGroupAsync(long userId, long friendId, int groupId,CancellationToken ct = default);

    /// <summary>
    /// 搜索好友（支持名称/备注搜索，游标分页）
    /// </summary>
    Task<CursorPage<FriendSearchResultDto>> SearchFriendsAsync(long userId, string searchTerm,
        string? cursor = null, int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// 获取指定用户已屏蔽的用户列表（游标分页）。
    /// </summary>
    /// <typeparam name="T">返回结果中每个元素的具体类型，由提供的选择器决定。</typeparam>
    /// <param name="userId">查询屏蔽列表的用户的ID。</param>
    /// <param name="selector">用于从BlockRecord对象中选择或投影特定属性的选择器表达式。</param>
    /// <param name="cursor">游标，首次请求传 null</param>
    /// <param name="limit">每页数量，默认 50，最大 100</param>
    /// <param name="ct">取消令牌，允许请求在完成前被取消。</param>
    Task<CursorPage<T>> GetBlockedUsersAsync<T>(long userId, Expression<Func<BlockRecord, T>> selector,
        string? cursor = null, int limit = 50, CancellationToken ct = default) where T : class;
}