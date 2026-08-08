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

    /// <summary>待验证的新邮箱；确认前不替换 <see cref="Email"/>。</summary>
    public string? PendingEmail { get; set; }
    /// <summary>待验证新邮箱的规范化形式。</summary>
    public string? NormalizedPendingEmail { get; set; }
    /// <summary>发起邮箱变更的时间（UTC）。</summary>
    public DateTimeOffset? PendingEmailRequestedAt { get; set; }

    // ---------- 密码 ----------
    /// <summary>BCrypt 哈希后的密码。</summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// 密码哈希格式/成本参数版本。旧数据为 0，成功登录后按需升级。
    /// </summary>
    public int PasswordHashVersion { get; set; }

    // ---------- 安全戳（用于并发保护） ----------
    public string? SecurityStamp { get; set; }
    /// <summary>
    /// 认证快照的单调递增版本。密码、角色、封禁和其它会话安全边界变化时递增，
    /// 用于给 AT/RT/Session 打版本栅栏，避免孤立缓存记录继续被轮换。
    /// </summary>
    // Only the infrastructure atomic advancer may mutate this value in production.
    // The internal setter also lets persistence materialization and in-memory tests
    // model a committed version without exposing a business-level mutator.
    public long SecurityVersion { get; internal set; } = 1;
    /// <summary>
    /// Monotonic account-deletion generation. In-flight attachment work carries
    /// this value and must abandon itself when the generation changes.
    /// </summary>
    public long DeletionEpoch { get; set; }
    public string? ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString();

    // ---------- 手机 ----------
    public string? PhoneNumber { get; set; }
    public string? NormalizedPhoneNumber { get; set; }
    public bool PhoneNumberConfirmed { get; set; }
    /// <summary>待验证的新手机号；确认前不替换 PhoneNumber。</summary>
    public string? PendingPhoneNumber { get; set; }
    public string? NormalizedPendingPhoneNumber { get; set; }
    public DateTimeOffset? PendingPhoneRequestedAt { get; set; }
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
    /// <summary>头像更新 fencing 版本；并发确认只能提交一个版本。</summary>
    public long AvatarVersion { get; set; } = 1;
    /// <summary>性别</summary>
    public bool Gender { get; set; }
    /// <summary>个性签名</summary>
    public string? Signature { get; set; }
    /// <summary>地区</summary>
    public string? Region { get; set; }

    public UserStatus Status { get; set; }

    /// <summary>谁可发送好友申请。</summary>
    public FriendRequestPolicy FriendRequestPolicy { get; set; } = FriendRequestPolicy.RequireVerification;

    /// <summary>好友申请站内/邮件通知偏好。</summary>
    public bool NotifyFriendRequests { get; set; } = true;

    /// <summary>账号生命周期状态；与 DeletionScheduledAt 一起兼容旧数据。</summary>
    public AccountState AccountState { get; set; } = AccountState.Active;

    /// <summary>是否允许出现在全局用户搜索中。</summary>
    public bool AllowBeSearched { get; set; } = true;

    /// <summary>安全事件邮件通知偏好。</summary>
    public bool NotifySecurityEmail { get; set; } = true;

    /// <summary>TOTP 密钥（AES-GCM 加密后存储；启用 2FA 后非空）。</summary>
    public string? TotpSecret { get; set; }

    /// <summary>待确认的 TOTP 密钥（加密）；确认前不覆盖 <see cref="TotpSecret"/>。</summary>
    public string? PendingTotpSecret { get; set; }

    /// <summary>恢复码 HMAC 摘要列表（JSON 数组，格式 v{version}:{base64url}）。</summary>
    public string? RecoveryCodesHashJson { get; set; }

    /// <summary>待确认的恢复码摘要（确认 MFA 后写入 <see cref="RecoveryCodesHashJson"/>）。</summary>
    public string? PendingRecoveryCodesHashJson { get; set; }

    /// <summary>计划注销时间；到点后由后台任务删除。</summary>
    public DateTimeOffset? DeletionScheduledAt { get; set; }

    /// <summary>注销处理租约截止时间（多实例抢占）。</summary>
    public DateTimeOffset? DeletionLeaseUntil { get; set; }

    /// <summary>注销处理租约持有者。</summary>
    public string? DeletionLeaseOwner { get; set; }

    /// <summary>注销处理租约的独立 fencing token；不能只用 owner/时间判断归属。</summary>
    public string? DeletionLeaseToken { get; set; }

    /// <summary>注销作业已领取/失败的持久尝试次数。</summary>
    public int DeletionAttemptCount { get; set; }

    /// <summary>注销作业下一次可领取时间。</summary>
    public DateTimeOffset? DeletionNextAttemptAt { get; set; }

    /// <summary>最近一次注销失败原因（不含敏感数据）。</summary>
    public string? DeletionLastError { get; set; }

    /// <summary>非空表示进入持久 DLQ，需管理员显式重放。</summary>
    public DateTimeOffset? DeletionDeadLetterAt { get; set; }

    /// <summary>封禁截止时间（风控/举报）；优先于普通锁定语义时用于业务拒绝。</summary>
    public DateTimeOffset? BanUntil { get; set; }

    /// <summary>安全事件“不是本人”后要求下次登录前修改密码。</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>上次修改用户名时间（用于冷却期）。</summary>
    public DateTimeOffset? UserNameChangedAt { get; set; }

    /// <summary>用户账户创建时间</summary>
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>用户最后一次成功登录的时间</summary>
    public DateTimeOffset? LastLoginDate { get; set; }
}

