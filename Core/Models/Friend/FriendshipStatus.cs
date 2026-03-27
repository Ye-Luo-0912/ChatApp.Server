namespace Core.Models.Friend;

public enum FriendshipStatus:byte
{
    /// <summary>
    /// 无好友关系
    /// </summary>
    None=0,
    /// <summary>
    /// 待批准 (Pending)
    /// </summary>
    Pending = 1,
    /// <summary>
    /// 已批准 (Approved)
    /// </summary>
    Approved = 2,
    /// <summary>
    /// 已拒绝 (Rejected)
    /// </summary>
    Rejected = 5,
}