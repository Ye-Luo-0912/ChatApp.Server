using Core.Models.Device;

namespace Core.Interfaces;

public interface IDeviceInfo
{
    /// <summary>
    ///     生成当前请求的设备信息
    /// </summary>
    DeviceInfo GenerateDeviceInfo();

    /// <summary>
    ///     获取设备ID
    /// </summary>
    /// <returns></returns>
    string? GetDeviceId();
}