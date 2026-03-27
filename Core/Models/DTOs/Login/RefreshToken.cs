namespace Core.Models.DTOs.Login;

public class RefreshToken
{

    /// <summary>
    /// 关联用户ID
    /// </summary>
    public required string UserId { get; set; }

    /// <summary>
    /// 设备唯一标识
    /// </summary>
    public required string DeviceId { get; init; }

    /// <summary>
    /// 令牌字符串
    /// </summary>
    public required string Token { get; set; }

    /// <summary>
    /// 令牌创建时间（UTC）
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// 过期时间（UTC）
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// 设备名称
    /// </summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// 客户端IP地址
    /// </summary>
    public string? ClientIp { get; set; }

    /// <summary>
    /// 是否已被撤销
    /// </summary>
    public bool IsRevoked { get; }

    /// <summary>
    /// 最后一次使用时间（UTC）
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// 检查令牌是否有效（未过期且未撤销）
    /// </summary>
    public bool IsValid => !IsRevoked && ExpiresAt > DateTime.UtcNow;
}