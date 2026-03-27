namespace Core.Models.Friend;

/// <summary>
/// 表示发送好友请求的结果状态。
/// </summary>
public enum SendFriendRequestOutcome
{
    /// <summary>
    /// 表示没有发送好友请求或操作未成功。
    /// </summary>
    None = 0,

    /// <summary>
    /// 表示好友请求已成功发送。
    /// </summary>
    RequestSent,

    /// <summary>
    /// 表示当前用户已经有一个待处理的好友请求发送给了目标用户，无需重复发送。
    /// </summary>
    RequestAlreadyPending,

    /// <summary>
    /// 表示好友请求被直接接受，无需等待对方确认。
    /// </summary>
    AcceptedDirectly,

    /// <summary>
    /// 表示之前被拒绝的好友请求已被直接恢复为待处理状态。
    /// </summary>
    RestoredDirectly,

    /// <summary>
    /// 表示好友关系已恢复。
    /// </summary>
    FriendshipRestored
}