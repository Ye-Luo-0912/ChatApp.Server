using Microsoft.AspNetCore.Identity;

namespace Core.Models.Identity;

public class ApplicationUser:IdentityUser<long>
{
    /// <summary>
    /// 生日 (可为空)
    /// </summary>
    public DateTime? Birthday { get; set; } // 
    /// <summary>
    /// 头像 URL (可为空)
    /// </summary>
    public string? AvatarUrl { get; set; } // 
    /// <summary>
    /// 性别
    /// </summary>
    public bool Gender { get; set; } // 
    /// <summary>
    /// 个性签名
    /// </summary>
    public string? Signature { get; set; } //个性签名
    /// <summary>
    /// 地区
    /// </summary>
    public string? Region { get; set; } //地区
    
    public UserStatus Status { get; set; }
    
    /// <summary>
    /// 用户账户创建时间
    /// </summary>
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
    
    /// <summary>
    /// 用户最后一次成功登录的时间
    /// </summary>
    public DateTimeOffset? LastLoginDate { get; set; }
}

