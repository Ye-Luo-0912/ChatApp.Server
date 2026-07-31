using System.Net;
using System.Net.Sockets;

namespace Infrastructure.Services;

/// <summary>对外通知和日志使用的 IP 脱敏显示；鉴权/风控内部仍可使用原始连接地址。</summary>
internal static class IpPrivacy
{
    public static string Display(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out var address))
            return "未知";

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0";
        }

        // IPv6 只显示地址族和前缀标识，不保留可直接定位的完整地址。
        return "IPv6/64";
    }
}
