using Core.Models.Identity;

namespace Core.Models.Friend;

/// <summary>
/// 表示用户好友列表中的一项条目，封装单条好友关系的详细信息。
/// 此类用于存储和管理用户之间的关联关系，包含好友关系创建时间、
/// 以及关联该好友关系的分组ID。
/// </summary>
public class UserFriendEntry
{
    /// <summary>
    /// 唯一标识一个友谊关系的ID。
    /// </summary>
    public long FriendshipId { get; set; }
    /// <summary>
    /// 用户ID
    /// </summary>
    public long UserId { get; set; } 
    /// <summary>
    /// 好友ID
    /// </summary>
    public long FriendId { get; set; } 
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    
    public bool IsDeleted { get; set; }
    
    public DateTime? DeletedAt { get; set; }
    public string? Note { get; set; }
    
    public int? GroupId { get; set; }
    
    // 导航属性
    public ApplicationUser? User { get; set; }
    public ApplicationUser? Friend { get; set; }
    
    public FriendGroup? Group { get; set; }
    
}