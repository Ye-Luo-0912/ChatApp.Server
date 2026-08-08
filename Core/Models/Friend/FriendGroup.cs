using Core.Models.Identity;

namespace Core.Models.Friend;

/// <summary>
/// 朋友分组
/// </summary>
public class FriendGroup
{
    /// <summary>
    /// 组ID，用于唯一标识一个好友分组。
    /// </summary>
    public int GroupId { get; set; }

    /// <summary>
    /// 用户ID
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// 组名称
    /// </summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>排序权重，越小越靠前。</summary>
    public int SortOrder { get; set; }

    /// <summary>是否为默认分组。</summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// 表示该好友组的所有者。
    /// </summary>
    public ApplicationUser? Owner { get; set; }
}
