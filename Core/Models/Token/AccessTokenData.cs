using System.Text.Json.Serialization;

namespace Core.Models.Token;

/// <summary>
/// 访问令牌载荷——最小集，仅保留每次 HTTP 请求认证所需字段。
/// <para>
/// 短属性名（"u" / "n" / "r" / "e"）降低 Redis 存储占用；
/// <see cref="IsExpired"/> 为计算属性，不参与序列化。
/// </para>
/// </summary>
public sealed class AccessTokenData
{
    /// <summary>用户 ID。</summary>
    [JsonPropertyName("u")]
    public required long UserId { get; set; }

    /// <summary>用户名。</summary>
    [JsonPropertyName("n")]
    public required string UserName { get; set; }

    /// <summary>角色列表；无角色时为 <see langword="null"/>，不写入 JSON。</summary>
    [JsonPropertyName("r")]
    public string[]? Roles { get; set; }

    /// <summary>过期时间（Unix 毫秒时间戳）。</summary>
    [JsonPropertyName("e")]
    public required long ExpiresAtMs { get; set; }

    /// <summary>所属会话的唯一标识；通过高阶方法签发时填充。</summary>
    [JsonPropertyName("s")]
    public string? SessionId { get; set; }

    /// <summary>
    /// 原始设备 ID 的 64 位 SHA-256 截断指纹（详见 <see cref="DeviceIdHashHelper"/>）。
    /// TCP 服务端凭此与客户端握手时提供的设备 ID 做比对校验，仅 8 字节，最小化 AT 存储开销。
    /// </summary>
    [JsonPropertyName("d")]
    public ulong? DeviceIdHash { get; set; }

    /// <summary>签发时的用户认证快照版本；版本变化时由会话撤销栅栏使其失效。</summary>
    [JsonPropertyName("v")]
    public long SecurityVersion { get; set; }

    /// <summary>是否已过期（运行时计算，不参与序列化）。</summary>
    [JsonIgnore]
    public bool IsExpired => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= ExpiresAtMs;
}
