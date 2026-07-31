using Core.Models.Identity;

namespace Core.Models.Token;

public sealed class LoginResult
{
    // ---------- 流程状态 ----------
    public bool IsSuccess { get; init; }
    public LoginCheckStatus LoginCheckStatus { get; init; }
    public string? ErrorMessage { get; init; }

    // ---------- 令牌对 ----------
    public string? AccessToken { get; init; }
    public DateTime AccessTokenExpiresAtUtc { get; init; }
    public string? RefreshToken { get; init; }
    public DateTime RefreshTokenExpiresAtUtc { get; init; }

    // ---------- 会话元信息 ----------
    /// <summary>服务端登录完成时间（UTC），可用于客户端时钟校准。</summary>
    public DateTimeOffset LoginAt { get; init; }
    /// <summary>上次成功登录时间（UTC），为空表示首次登录。客户端可据此提示异地登录。</summary>
    public DateTimeOffset? PreviousLoginDate { get; init; }
    /// <summary>本次登录的客户端 IP。</summary>
    public string? ClientIp { get; init; }
    /// <summary>是否为该设备首次出现的会话（新设备登录提醒）。</summary>
    public bool IsNewDevice { get; init; }
    /// <summary>
    /// 热路径 IP 粗信号：当前 IP 与既有会话不一致。
    /// 正式「异常地点」判定与通知由异步 LoginRiskAnalyzer（地理 + 可信设备/历史）完成。
    /// </summary>
    public bool IsUnusualLocation { get; init; }

    /// <summary>登录时轮换后的可信设备令牌（明文仅此一次）；客户端应覆盖本地存储。</summary>
    public string? TrustedDeviceToken { get; init; }

    /// <summary>登录时签发的设备凭据明文；客户端应安全保存并在刷新时携带。</summary>
    public string? DeviceCredential { get; init; }

    /// <summary>
    /// 仍存有旧版 BCrypt 恢复码摘要时为 true；客户端应引导用户重新生成恢复码（新码为 HMAC）。
    /// </summary>
    public bool RequiresRecoveryCodeRegeneration { get; init; }

    /// <summary>本次登录的会话唯一标识；TCP 握手时可携带，用于会话关联。</summary>
    public string? SessionId { get; init; }
    /// <summary>
    /// 设备指纹的 64 位哈希（由服务端计算并直接下发）。
    /// 客户端将此值原样携带至 TCP 握手，TCP 侧做整数比对，无需重新计算 hash。
    /// 完整设备信息（设备名、IP 等）可通过 SessionId 关联的会话包获取。
    /// </summary>
    public ulong? DeviceIdHash { get; init; }

    /// <summary>需要 MFA 时返回，客户端携带至 /api/auth/mfa/verify。</summary>
    public string? MfaToken { get; init; }
    public bool RequiresTwoFactor { get; init; }

    // ---------- 用户画像快照（避免客户端登录后再发一次 /profile 请求） ----------
    public long? UserId { get; init; }
    public string? UserName { get; init; }
    public string? Email { get; init; }
    public string? AvatarUrl { get; init; }
    public string? Signature { get; init; }
    public bool Gender { get; init; }
    public string? Region { get; init; }
    public UserStatus Status { get; init; }

    // ---------- 实时通信连接端点 ----------
    public ServerEndPoint? Server { get; init; }

    /// <summary>
    /// 工厂方法：登录失败。
    /// </summary>
    public static LoginResult Fail(string error, LoginCheckStatus status) => new()
    {
        IsSuccess = false,
        ErrorMessage = error,
        LoginCheckStatus = status
    };

    public static LoginResult RequireMfa(long userId, string mfaToken) => new()
    {
        IsSuccess = false,
        LoginCheckStatus = LoginCheckStatus.RequiresTwoFactor,
        ErrorMessage = "需要两步验证",
        UserId = userId,
        MfaToken = mfaToken,
        RequiresTwoFactor = true,
    };

    /// <summary>
    /// 工厂方法：创建一个表示登录成功的 LoginResult 实例。
    /// </summary>
    /// <param name="user">与登录相关的用户信息。</param>
    /// <param name="previousLoginDate">用户上次登录的日期和时间，如果存在的话。</param>
    /// <param name="sessionId">会话标识符。</param>
    /// <param name="deviceIdHash">设备ID的哈希值。</param>
    /// <param name="accessToken">访问令牌。</param>
    /// <param name="accessTokenExpiresAtUtc">访问令牌过期的UTC时间。</param>
    /// <param name="refreshToken">刷新令牌。</param>
    /// <param name="refreshTokenExpiresAtUtc">刷新令牌过期的UTC时间。</param>
    /// <param name="server">引用服务器端点。</param>
    /// <returns>一个包含成功登录详细信息的 LoginResult 实例。</returns>
    public static LoginResult Success(
        ApplicationUser user,
        DateTimeOffset? previousLoginDate,
        string? sessionId,
        ulong? deviceIdHash,
        string accessToken,
        DateTime accessTokenExpiresAtUtc,
        string refreshToken,
        DateTime refreshTokenExpiresAtUtc,
        ref ServerEndPoint server,
        string? clientIp = null,
        bool isNewDevice = false,
        bool isUnusualLocation = false,
        string? trustedDeviceToken = null,
        bool requiresRecoveryCodeRegeneration = false,
        string? deviceCredential = null
    ) => new()
    {
        IsSuccess = true,
        LoginCheckStatus = LoginCheckStatus.Success,
        LoginAt                  = DateTimeOffset.UtcNow,
        PreviousLoginDate        = previousLoginDate,
        ClientIp                 = clientIp,
        IsNewDevice              = isNewDevice,
        IsUnusualLocation        = isUnusualLocation,
        TrustedDeviceToken       = trustedDeviceToken,
        DeviceCredential         = deviceCredential,
        RequiresRecoveryCodeRegeneration = requiresRecoveryCodeRegeneration,
        SessionId                = sessionId,
        DeviceIdHash             = deviceIdHash,
        UserId                   = user.Id,
        UserName                 = user.UserName,
        Email                    = user.Email,
        AvatarUrl                = user.AvatarUrl,
        Signature                = user.Signature,
        Gender                   = user.Gender,
        Region                   = user.Region,
        Status                   = user.Status,
        AccessToken              = accessToken,
        AccessTokenExpiresAtUtc  = accessTokenExpiresAtUtc,
        RefreshToken             = refreshToken,
        RefreshTokenExpiresAtUtc = refreshTokenExpiresAtUtc,
        Server                   = server
    };
}

/// <summary>
/// 登录状态枚举，表示登录验证的结果状态
/// </summary>
public enum LoginCheckStatus:byte
{
    /// <summary>
    /// 表示登录验证成功，用户凭据有效，可以正常登录系统。
    /// </summary>
    Success =   1,
    /// <summary>
    /// 表示由于凭据无效，认证失败。
    /// </summary>
    InvalidCredentials = 2,
    /// <summary>
    /// 表示由于多次失败尝试，账户被锁定。用户需要等待一段时间或联系管理员解锁后才能再次尝试登录。
    /// </summary>
    LockedOut = 3,
    /// <summary>
    /// 表示用户被禁止登录。这可能是由于管理员禁用了账户，或者用户违反了服务条款等原因导致的。被禁止登录的用户无法访问系统，除非管理员重新启用账户。
    /// </summary>
    NotAllowed = 4,
    /// <summary>
    /// 表示用户需要通过两步验证才能登录。这通常发生在用户启用了两步验证但尚未完成验证过程时。
    /// </summary>
    RequiresTwoFactor = 5,
    /// <summary>
    /// 表示密码校验过载（BCrypt 并发闸门），应快速失败并稍后重试。
    /// </summary>
    Overloaded = 6
}

