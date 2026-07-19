namespace Core.Models.Identity;

/// <summary>
/// 用户-角色关联（对应 AspNetUserRoles 表）。
/// </summary>
public class UserRole
{
    public long UserId { get; set; }
    public long RoleId { get; set; }

    public ApplicationUser User { get; set; } = null!;
    public ApplicationRoles Role { get; set; } = null!;
}
