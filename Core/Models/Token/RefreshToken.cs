using System.Text.Json.Serialization;

namespace Core.Models.Token;

/// <summary>
/// 刷新令牌记录——中等层，只保留校验与轮换时实际读回的字段。
/// <para>
/// 省略的字段说明：<br/>
/// · UserId / Token — 已编码在 Redis 键 <c>RT:{userId}:{hash}</c> 中，无需重复存储；<br/>
/// · IsRevoked — 撤销直接删 Key，不存软标记；<br/>
/// · CreatedAt / LastUsedAt / DeviceName / DeviceType / ClientIp / UserAgent
///   — 应用逻辑从不读回这些字段，审计数据由 <see cref="SessionRecord"/> 保留。
/// </para>
/// </summary>
public sealed class RefreshToken
{
    /// <summary>设备唯一标识；用于跨设备复用校验和会话键定位。</summary>
    [JsonPropertyName("d")]
    public required string DeviceId { get; init; }

    /// <summary>过期时间（Unix 毫秒时间戳）。</summary>
    [JsonPropertyName("e")]
    public required long ExpiresAtMs { get; set; }

    /// <summary>原始登录时间（UTC）；令牌轮换时继承，用于追溯会话起点。</summary>
    [JsonPropertyName("la")]
    public DateTime LoginAt { get; set; }

    /// <summary>自签发以来的轮换次数（每次 Rotate +1）。</summary>
    [JsonPropertyName("n")]
    public int RefreshCount { get; set; }

    /// <summary>所属会话的唯一标识；轮换时继承。</summary>
    [JsonPropertyName("s")]
    public string? SessionId { get; set; }

    /// <summary>当前绑定访问令牌的 Redis 键（<c>AT:{hash}</c>）；轮换时用于撤销旧访问令牌。</summary>
    [JsonPropertyName("at")]
    public string? CurrentAccessTokenKey { get; set; }

    /// <summary>令牌是否仍然有效：未过期（Redis TTL 为主要保障，此为兜底）。</summary>
    [JsonIgnore]
    public bool IsValid => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < ExpiresAtMs;
}
