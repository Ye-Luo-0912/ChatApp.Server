namespace Core.Models.Friend;

/// <summary>
/// 好友关系状态信息
/// </summary>
public class FriendshipStatusInfo
{
    /// <summary>
    /// 当前状态
    /// </summary>
    public FriendshipStatus Status { get; set; }
    /// <summary>
    /// 获取或设置好友关系建立的日期和时间。
    /// 此属性为可空的 DateTime 类型，
    /// 当好友关系尚未建立（例如处于待批准状态）时，该值可以为 null。
    /// </summary>
    public DateTime? EstablishedDate { get; set; }
    /// <summary>
    /// 获取或设置一个布尔值，指示好友关系是否为双向的。
    /// 如果双方都确认了好友关系，该属性值为 true；否则为 false。
    /// </summary>
    public bool IsMutual { get; set; }

    /// <summary>
    /// 任一方向存在 BlockRecord。该信号必须在好友/共享会话成员授权之前优先拒绝。
    /// </summary>
    public bool IsBlocked { get; set; }
}
