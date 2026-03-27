using Core.Models.Identity;

namespace Core.Models.Friend;

/// <summary>
/// 该类表示好友请求的实体，用于存储好友请求的相关信息，
/// 包括请求的基本信息、状态以及关联的用户信息。
/// </summary>
public class FriendRequest
{
    /// <summary>
    /// 获取或设置好友请求的唯一标识符。
    /// 该值用于在数据库中唯一标识一条好友请求记录。
    /// </summary>
    public long RequestId { get; set; }

    /// <summary>
    /// 获取或设置发起好友请求的用户的唯一标识符。
    /// 该值对应于发起请求的用户在系统中的 ID。
    /// </summary>
    public long RequesterId { get; set; }

    /// <summary>
    /// 获取或设置被请求成为好友的目标用户的唯一标识符。
    /// 该值对应于目标用户在系统中的 ID。
    /// </summary>
    public long TargetUserId { get; set; }

    /// <summary>
    /// 获取或设置好友请求中附带的消息。
    /// 该消息由发起请求的用户提供，可为空，默认值为空字符串。
    /// </summary>
    public string? Message { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置好友请求的当前状态。
    /// 状态使用 RequestStatus 枚举表示，
    /// 可能的值包括待处理（Pending）、已接受（Accepted）、已拒绝（Declined）。
    /// </summary>
    public RequestStatus Status { get; set; }

    /// <summary>
    /// 获取或设置好友请求的创建时间。
    /// 该时间记录了请求发起的具体时刻。
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 获取或设置好友请求的响应时间。
    /// 该时间记录了目标用户对请求进行响应（接受或拒绝）的具体时刻，可为空。
    /// 当请求处于待处理状态时，该值为 null。
    /// </summary>
    public DateTime? RespondedAt { get; set; }

    /// <summary>
    /// 获取或设置发起好友请求的用户实体。
    /// 这是一个导航属性，用于关联发起请求的用户对象，可能为 null。
    /// </summary>
    public ApplicationUser? Requester { get; set; }

    /// <summary>
    /// 获取或设置被请求成为好友的目标用户实体。
    /// 这是一个导航属性，用于关联目标用户对象，可能为 null。
    /// </summary>
    public ApplicationUser? TargetUser { get; set; }
}