namespace Core.Models.Identity;

public class ApplicationUser
{
    // ---------- 主键 ----------
    public long Id { get; set; }

    // ---------- 账号标识 ----------
    public string? UserName { get; set; }
    public string? NormalizedUserName { get; set; }
    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }
    public bool EmailConfirmed { get; set; }

    // ---------- 密码 ----------
    /// <summary>BCrypt 哈希后的密码。</summary>
    public string? PasswordHash { get; set; }

    // ---------- 安全戳（用于并发保护） ----------
    public string? SecurityStamp { get; set; }
    public string? ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString();

    // ---------- 手机 ----------
    public string? PhoneNumber { get; set; }
    public bool PhoneNumberConfirmed { get; set; }
    public bool TwoFactorEnabled { get; set; }

    // ---------- 账户锁定 ----------
    public DateTimeOffset? LockoutEnd { get; set; }
    public bool LockoutEnabled { get; set; } = true;
    public int AccessFailedCount { get; set; }

    // ---------- 业务字段 ----------
    /// <summary>生日 (可为空)</summary>
    public DateTime? Birthday { get; set; }
    /// <summary>头像 URL (可为空)</summary>
    public string? AvatarUrl { get; set; }
    /// <summary>性别</summary>
    public bool Gender { get; set; }
    /// <summary>个性签名</summary>
    public string? Signature { get; set; }
    /// <summary>地区</summary>
    public string? Region { get; set; }

    public UserStatus Status { get; set; }

    /// <summary>用户账户创建时间</summary>
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>用户最后一次成功登录的时间</summary>
    public DateTimeOffset? LastLoginDate { get; set; }
}

