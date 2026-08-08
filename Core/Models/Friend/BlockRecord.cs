using Core.Models.Identity;

namespace Core.Models.Friend;

/// <summary>
/// 表示应用程序内用户屏蔽其他用户的记录。
/// 此类用于存储屏蔽方（Blocker）屏蔽被屏蔽方（BlockedUser）的相关信息，
/// 包含执行屏蔽操作的时间戳。
/// </summary>
public class BlockRecord
{
    public long BlockId { get; set; } 
    public long BlockerId { get; set; }
    public long BlockedUserId { get; set; }
    public DateTime BlockedAt { get; set; }
    
    // 导航属性
    public ApplicationUser? Blocker { get; set; }
    public ApplicationUser? BlockedUser { get; set; }
}
