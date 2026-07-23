namespace Core.Settings;

public sealed class TrustedDeviceOptions
{
    public const string SectionName = "TrustedDevices";

    /// <summary>每用户有效可信设备上限；达到上限时拒绝新增（须先移除旧设备）。</summary>
    public int MaxDevicesPerUser { get; set; } = 10;

    /// <summary>
    /// LastSeenAt 写放大节流：仅当库内 LastSeenAt 早于 now - 该窗口时才 UPDATE。
    /// 默认 1 小时；窗口内校验不改写行。
    /// </summary>
    public double LastSeenThrottleHours { get; set; } = 1;
}
