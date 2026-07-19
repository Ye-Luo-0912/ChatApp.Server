namespace Core.Models.Device;

public class DeviceInfo
{
    /// <summary>
    /// 设备唯一标识
    /// </summary>
    public required string DeviceId { get; set; }

    /// <summary>
    /// 设备名称
    /// </summary>
    public required string DeviceName { get; set; }

    /// <summary>
    /// 最后登录时间（UTC）
    /// </summary>
    public DateTime LastLogin { get; set; }

    /// <summary>
    /// 登录IP地址
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// 是否当前设备
    /// </summary>
    public bool IsCurrentDevice { get; set; }

    /// <summary>
    /// 设备类型（自动识别，如 Mobile/Desktop）
    /// </summary>
    public string? DeviceType { get; set; }

    /// <summary>
    /// 客户端原始 User-Agent 字符串，用于设备指纹生成和安全审计。
    /// </summary>
    public string? UserAgent { get; set; }
}