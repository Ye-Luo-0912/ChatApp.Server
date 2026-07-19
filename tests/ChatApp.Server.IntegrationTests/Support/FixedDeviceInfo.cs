using Core.Interfaces;
using Core.Models.Device;

namespace ChatApp.Server.IntegrationTests.Support;

/// <summary>
/// 测试用固定设备指纹，保证并发刷新落在同一设备绑定上。
/// </summary>
internal sealed class FixedDeviceInfo(string deviceId) : IDeviceInfo
{
    public DeviceInfo GenerateDeviceInfo() => new()
    {
        DeviceId = deviceId,
        DeviceName = "IntegrationTest",
        DeviceType = "Desktop",
        LastLogin = DateTime.UtcNow,
        IpAddress = "127.0.0.1",
        UserAgent = "integration-test",
        IsCurrentDevice = true,
    };

    public string? GetDeviceId() => deviceId;
}
