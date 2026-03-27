using System.Security.Cryptography;
using System.Text;
using Core.Interfaces;
using Core.Models.Device;
using Microsoft.AspNetCore.Http;
using UAParser;
using UAParser.Objects;

namespace Infrastructure.Services;

public sealed class DeviceInfoService(IHttpContextAccessor httpContextAccessor) : IDeviceInfo
{
    private const string Salt = "SecureSaltHere_2024#Optimized";
    private static readonly Lazy<Parser> UaParser = new(Parser.GetDefault());

    private static readonly HashSet<string> MobileKeywords = new(StringComparer.OrdinalIgnoreCase)
        { "phone", "mobile", "android", "ios", "tablet" };

    private static readonly HashSet<string> DesktopKeywords = new(StringComparer.OrdinalIgnoreCase)
        { "windows", "mac", "linux", "desktop", "pc" };

    public DeviceInfo GenerateDeviceInfo()
    {
        var context = httpContextAccessor.HttpContext;
        var userAgent = context?.Request.Headers.UserAgent.ToString() ?? string.Empty;
        var acceptLanguage = context?.Request.Headers.AcceptLanguage.ToString() ?? string.Empty;
        var clientIp = GetClientIpAddress(context);

        var clientInfo = UaParser.Value.Parse(userAgent);

        return new DeviceInfo
        {
            DeviceId = GenerateDeviceId(userAgent, acceptLanguage),
            DeviceName = GetDeviceName(clientInfo),
            LastLogin = DateTime.UtcNow,
            IpAddress = clientIp,
            IsCurrentDevice = true,
            DeviceType = GetDeviceType(clientInfo)
        };
    }

    public string GetDeviceId()
    {
        var context = httpContextAccessor.HttpContext;
        var userAgent = context?.Request.Headers.UserAgent.ToString() ?? string.Empty;
        var acceptLanguage = context?.Request.Headers.AcceptLanguage.ToString() ?? string.Empty;

        return GenerateDeviceId(userAgent, acceptLanguage);
    }

    private static string GenerateDeviceId(string userAgent, string acceptLanguage)
    {
        var capacity = Salt.Length + userAgent.Length + acceptLanguage.Length + 3;
        var fingerprintBuilder = new StringBuilder(capacity)
            .Append(Salt).Append('|')
            .Append(userAgent).Append('|')
            .Append(acceptLanguage);

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintBuilder.ToString()));
        return Convert.ToBase64String(hashBytes);
    }

    private static string? GetClientIpAddress(HttpContext? context)
    {
        if (context == null) return null;

        var headers = new[] { "CF-Connecting-IP", "X-Forwarded-For", "X-Real-IP" };
        foreach (var header in headers)
            if (context.Request.Headers.TryGetValue(header, out var values) && !string.IsNullOrEmpty(values))
                return values.First()?.Split(',').FirstOrDefault()?.Trim();

        return context.Connection.RemoteIpAddress?.ToString();
    }

    private static string GetDeviceName(ClientInfo? clientInfo)
    {
        if (clientInfo?.Device == null) return "Unknown Device";

        // 使用快速字符串拼接
        var sb = new StringBuilder(32);
        AppendDeviceInfo(sb, clientInfo.Device.Brand);
        AppendDeviceInfo(sb, clientInfo.Device.Model);

        if (sb.Length > 0) return sb.ToString();

        // 操作系统回退逻辑
        sb.Append(clientInfo.OS.Family);
        AppendVersion(sb, clientInfo.OS.Major, clientInfo.OS.Minor);
        return sb.Append(" Device").ToString();
    }

    private static string GetDeviceType(ClientInfo? clientInfo)
    {
        if (clientInfo?.Device == null) return "Other";

        // 单次小写转换
        var osFamily = clientInfo.OS.Family?.ToLowerInvariant() ?? "";
        var deviceFamily = clientInfo.Device.Family.ToLowerInvariant();

        // 使用HashSet快速判断
        if (ContainsAny(osFamily, MobileKeywords) || ContainsAny(deviceFamily, MobileKeywords))
            return "Mobile";

        if (ContainsAny(deviceFamily, DesktopKeywords))
            return "Desktop";

        return clientInfo.Device.IsSpider ? "Spider" : "Other";
    }

    ~DeviceInfoService()
    {
        httpContextAccessor.HttpContext = null;
        MobileKeywords.Clear();
        DesktopKeywords.Clear();
    }

    #region 辅助方法

    private static void AppendDeviceInfo(StringBuilder sb, string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Equals("other", StringComparison.OrdinalIgnoreCase))
            return;

        if (sb.Length > 0) sb.Append(' ');
        sb.Append(value);
    }

    private static void AppendVersion(StringBuilder sb, string? major, string? minor)
    {
        if (string.IsNullOrEmpty(major)) return;

        sb.Append(' ').Append(major);
        if (!string.IsNullOrEmpty(minor))
            sb.Append('.').Append(minor);
    }

    private static bool ContainsAny(string source, HashSet<string> keywords)
    {
        return keywords.Any(keyword => source.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    #endregion
}