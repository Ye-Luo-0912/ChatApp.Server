namespace Core.Models.Security;

/// <summary>
/// 用户主动信任的设备。仅保存高熵令牌哈希，不信任客户端可伪造的 X-Device-Id 作为鉴权凭据。
/// </summary>
public sealed class TrustedDevice
{
    public long Id { get; set; }
    public long UserId { get; set; }

    /// <summary>可选的客户端设备标签（仅展示，不可用于鉴权）。</summary>
    public string? DeviceIdHint { get; set; }

    /// <summary>高熵可信设备令牌的 SHA-256 十六进制哈希。</summary>
    public string TokenHash { get; set; } = string.Empty;

    public string? Label { get; set; }
    public string? ClientIp { get; set; }
    public DateTimeOffset TrustedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
