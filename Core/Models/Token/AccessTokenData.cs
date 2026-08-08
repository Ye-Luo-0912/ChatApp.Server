using System.Globalization;
using System.Text.Json.Serialization;
using Core.Models.Identity;

namespace Core.Models.Token;

/// <summary>
/// 访问令牌载荷——只保留会话绑定和安全版本。
/// <para>
/// 用户名、角色和账户状态属于用户级授权快照，不应在每个会话的 AT 中重复保存。
/// <see cref="UserName"/> 和 <see cref="Roles"/> 只保留用于读取旧缓存数据的兼容字段；
/// 新签发的令牌不会填充它们。
/// 此类型是 Core 的运行时视图，不是 Redis wire contract；Infrastructure 在缓存边界
/// 显式映射到版本化的 ChatApp.Auth.Contracts.AccessTokenCacheRecord。
/// </para>
/// </summary>
public sealed class AccessTokenData
{
    private string? _userIdText;
    private string? _deviceIdHashText;

    /// <summary>用户 ID。</summary>
    public required long UserId { get; set; }

    /// <summary>
    /// 旧版令牌兼容字段。新令牌不填充，因此不会写入 Redis；认证时优先使用授权快照。
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// 旧版令牌兼容字段；新令牌不填充。角色不能作为新令牌的授权来源。
    /// </summary>
    public string[]? Roles { get; set; }

    /// <summary>过期时间（Unix 毫秒时间戳）。</summary>
    public required long ExpiresAtMs { get; set; }

    /// <summary>所属会话的唯一标识；通过高阶方法签发时填充。</summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// 原始设备 ID 的 64 位 SHA-256 截断指纹（详见 <see cref="DeviceIdHashHelper"/>）。
    /// TCP 服务端凭此与客户端握手时提供的设备 ID 做比对校验，仅 8 字节，最小化 AT 存储开销。
    /// </summary>
    public ulong? DeviceIdHash { get; set; }

    /// <summary>签发时的用户认证快照版本；版本变化时由会话撤销栅栏使其失效。</summary>
    public long SecurityVersion { get; set; }

    /// <summary>
    /// 旧版令牌兼容字段。账户状态始终从授权快照读取。
    /// </summary>
    public AccountState AccountState { get; set; } = AccountState.Active;

    /// <summary>
    /// Cached claim values. They are derived from the immutable token payload
    /// and deliberately excluded from Redis serialization; after the first
    /// warmed request they avoid formatting allocations on every request.
    /// </summary>
    [JsonIgnore]
    public string UserIdText =>
        _userIdText ??= UserId.ToString(CultureInfo.InvariantCulture);

    [JsonIgnore]
    public string? DeviceIdHashText =>
        DeviceIdHash is not { } value
            ? null
            : _deviceIdHashText ??= value.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>是否已过期（运行时计算，不参与序列化）。</summary>
    [JsonIgnore]
    public bool IsExpired => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= ExpiresAtMs;
}
