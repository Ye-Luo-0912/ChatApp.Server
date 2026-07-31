namespace Core.Models.Token;

/// <summary>
/// 会话记录——完整层，供设备管理、TCP 关联及安全审计使用。
/// <para>
/// 键：<c>SS:{userId}:{deviceId}</c>，每个用户+设备组合对应一条记录，
/// 在刷新令牌轮换时更新（而非重建），有效期与刷新令牌保持一致。
/// </para>
/// </summary>
public sealed class SessionRecord
{
    // ── 身份 ──────────────────────────────────────────────────────────────────

    /// <summary>所属用户 ID。</summary>
    public required string UserId { get; set; }

    // ── 时间 ──────────────────────────────────────────────────────────────────

    /// <summary>原始登录时间（UTC）；令牌轮换时继承，用于追踪会话起点。</summary>
    public DateTime LoginAt { get; set; }

    /// <summary>最近一次活跃时间（UTC）；每次令牌轮换后更新。</summary>
    public DateTime LastActiveAt { get; set; }

    /// <summary>会话到期时间（UTC）；随刷新令牌到期时间同步刷新。</summary>
    public DateTime ExpiresAt { get; set; }

    // ── 设备信息 ──────────────────────────────────────────────────────────────

    /// <summary>设备唯一标识（SHA-256 Base64url，来自 IDeviceInfo）。</summary>
    public required string DeviceId { get; init; }

    /// <summary>可读设备名称（如 "Chrome on Windows 11"）。</summary>
    public string? DeviceName { get; set; }

    /// <summary>设备类型（Mobile / Desktop / Bot / Other）。</summary>
    public string? DeviceType { get; set; }

    // ── 网络信息 ──────────────────────────────────────────────────────────────

    /// <summary>客户端 IP 地址（最新一次请求）。</summary>
    public string? ClientIp { get; set; }

    /// <summary>原始 User-Agent 字符串（最新一次请求）。</summary>
    public string? UserAgent { get; set; }

    // ── TCP 服务信息 ──────────────────────────────────────────────────────────

    /// <summary>当前分配的 TCP 服务器地址（如 "tcp://host:port"）；未分配时为 <see langword="null"/>。</summary>
    public string? TcpServer { get; set; }

    // ── 审计 ──────────────────────────────────────────────────────────────────
    /// <summary>所属会话的唯一标识，跟随刷新令牌轮换继承。</summary>
    public string? SessionId { get; set; }

    /// <summary>服务端设备凭据摘要；不保存可直接用于认证的明文。</summary>
    public string? DeviceCredentialHash { get; set; }

    /// <summary>当前活跃访问令牌的 Redis 键（<c>AT:{hash}</c>）；撤销会话时用于同步删除访问令牌。</summary>
    public string? CurrentAccessTokenKey { get; set; }

    /// <summary>当前活跃刷新令牌的 Redis 键（<c>RT:{userId}:{hash}</c>）；撤销会话时用于同步删除刷新令牌。</summary>
    public string? CurrentRefreshTokenKey { get; set; }
    /// <summary>令牌轮换次数（每次 Rotate +1），与 <see cref="RefreshToken.RefreshCount"/> 保持同步。</summary>
    public int RefreshCount { get; set; }

    /// <summary>建立会话时的用户认证快照版本。</summary>
    public long SecurityVersion { get; set; }

    /// <summary>会话是否仍处于活跃状态（主动注销后置 <see langword="false"/>）。</summary>
    public bool IsActive { get; set; } = true;
}
