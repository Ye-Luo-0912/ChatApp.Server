using System.Net;
using System.Security.Cryptography;
using System.Text;
using Core.Interfaces;
using Core.Models.Device;
using Microsoft.AspNetCore.Http;
using UAParser;
using UAParser.Objects;

namespace Infrastructure.Services;

/// <summary>
/// 从当前 HTTP 请求上下文中提取并生成设备信息。
/// <para>
/// 设备指纹（DeviceId）= SHA-256(盐 | User-Agent | Accept-Language) → Base64url，
/// 同一浏览器/设备在不同请求间保持稳定，跨设备或浏览器则自然区分。
/// </para>
/// <para>
/// 注册为<b>单例</b>；UA 解析与 DeviceInfo 对象在同一请求内通过
/// <see cref="HttpContext.Items"/> 缓存，避免多次调用重复解析。
/// </para>
/// </summary>
public sealed class DeviceInfoService(IHttpContextAccessor httpContextAccessor) : IDeviceInfo
{
    // 盐值防止彩虹表碰撞；修改会使所有历史 DeviceId 失效，请谨慎变更
    private const string FingerprintSalt = "ChatApp#DeviceFingerprint#2024";

    // UAParser 初始化代价高，全局只创建一次，Lazy 默认线程安全（ExecutionAndPublication）
    private static readonly Lazy<Parser> UaParser = new(() => Parser.GetDefault());

    // 用 object 类型作为 HttpContext.Items 键，避免字符串 key 碰撞
    private static readonly object DeviceInfoItemKey = new();
    private static readonly object ParsedUaItemKey   = new();

    // 移动端信号集合（UAParser OS / Device family 维度）
    private static readonly HashSet<string> MobileSignals = new(StringComparer.OrdinalIgnoreCase)
        { "android", "ios", "iphone", "ipad", "mobile", "phone", "tablet" };

    // 桌面端信号集合（UAParser device family 维度）
    private static readonly HashSet<string> DesktopSignals = new(StringComparer.OrdinalIgnoreCase)
        { "windows", "mac", "linux", "ubuntu", "fedora", "debian", "desktop" };

    // ─────────────────────────────────────────────────────────────────────────
    // IDeviceInfo 实现
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public DeviceInfo GenerateDeviceInfo()
    {
        var context = httpContextAccessor.HttpContext;

        // 同一请求的二次调用直接返回缓存，避免重复解析
        if (context?.Items[DeviceInfoItemKey] is DeviceInfo cached)
            return cached;

        var userAgent      = GetRawUserAgent(context);
        var acceptLanguage = GetAcceptLanguage(context);
        var clientInfo     = ParseUserAgent(context, userAgent);
        var clientIp       = ExtractClientIp(context);

        var info = new DeviceInfo
        {
            // 优先使用客户端生成的稳定设备 ID（X-Device-Id），缺失时回退 UA/语言指纹。
            DeviceId        = ResolveDeviceId(context, userAgent, acceptLanguage),
            DeviceName      = BuildDeviceName(clientInfo),
            DeviceType      = DetectDeviceType(userAgent, clientInfo),
            IpAddress       = clientIp,
            UserAgent       = userAgent,
            LastLogin       = DateTime.UtcNow,
            IsCurrentDevice = true,
        };

        if (context is not null)
            context.Items[DeviceInfoItemKey] = info;

        return info;
    }

    /// <inheritdoc />
    public string? GetDeviceId()
    {
        var context = httpContextAccessor.HttpContext;
        if (context is null) return null;

        var userAgent      = GetRawUserAgent(context);
        var acceptLanguage = GetAcceptLanguage(context);
        return ResolveDeviceId(context, userAgent, acceptLanguage);
    }

    /// <summary>
    /// 读取客户端提供的稳定设备 ID；需为 URL 安全、长度 16～128 的随机串。
    /// </summary>
    private static string ResolveDeviceId(HttpContext? context, string userAgent, string acceptLanguage)
    {
        if (context?.Request.Headers.TryGetValue("X-Device-Id", out var header) == true)
        {
            var clientId = header.ToString().Trim();
            if (IsValidClientDeviceId(clientId))
                return clientId;
        }

        return ComputeDeviceId(userAgent, acceptLanguage);
    }

    private static bool IsValidClientDeviceId(string value)
    {
        if (value.Length is < 16 or > 128)
            return false;

        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')
                continue;
            return false;
        }

        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 请求信息提取
    // ─────────────────────────────────────────────────────────────────────────

    private static string GetRawUserAgent(HttpContext? context)
        => context?.Request.Headers.UserAgent.ToString() ?? string.Empty;

    private static string GetAcceptLanguage(HttpContext? context)
        => context?.Request.Headers.AcceptLanguage.ToString() ?? string.Empty;

    /// <summary>
    /// UA 解析开销较高，结果缓存在 HttpContext.Items 内，同一请求只解析一次。
    /// </summary>
    private static ClientInfo ParseUserAgent(HttpContext? context, string userAgent)
    {
        if (context?.Items[ParsedUaItemKey] is ClientInfo cached)
            return cached;

        var result = UaParser.Value.Parse(userAgent);

        if (context is not null)
            context.Items[ParsedUaItemKey] = result;

        return result;
    }

    /// <summary>
    /// 提取客户端真实 IP，优先读取反代/CDN 注入的头，并验证格式合法性。
    /// 优先级：Cloudflare → X-Forwarded-For → X-Real-IP → RemoteIpAddress。
    /// </summary>
    private static string? ExtractClientIp(HttpContext? context)
    {
        if (context is null) return null;

        ReadOnlySpan<string> proxyHeaders = ["CF-Connecting-IP", "X-Forwarded-For", "X-Real-IP"];

        foreach (var header in proxyHeaders)
        {
            if (!context.Request.Headers.TryGetValue(header, out var values))
                continue;

            // X-Forwarded-For: client, proxy1, proxy2 → 取最左侧（最接近真实客户端）
            var raw      = values.ToString();
            var commaIdx = raw.IndexOf(',');
            var candidate = (commaIdx >= 0 ? raw[..commaIdx] : raw).Trim();

            if (IPAddress.TryParse(candidate, out _))
                return candidate;
        }

        // 直连场景：IPv4-mapped IPv6（如 ::ffff:1.2.3.4）转换为标准 IPv4 字符串
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null) return null;

        return remote.IsIPv4MappedToIPv6
            ? remote.MapToIPv4().ToString()
            : remote.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 设备指纹
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 计算设备指纹 = SHA-256(盐 | UA | AcceptLanguage)，输出 Base64url（无填充）。
    /// Base64url 编码与 <see cref="Infrastructure.Services.Auth.TokenService"/> 保持一致，
    /// 可直接用作 Redis 键组成部分。
    /// </summary>
    private static string ComputeDeviceId(string userAgent, string acceptLanguage)
    {
        var capacity = FingerprintSalt.Length + userAgent.Length + acceptLanguage.Length + 3;
        var input    = new StringBuilder(capacity)
            .Append(FingerprintSalt).Append('|')
            .Append(userAgent).Append('|')
            .Append(acceptLanguage)
            .ToString();

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(hash)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 设备名称构建
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 按优先级构建人类可读设备名称：
    /// ① 品牌 + 型号（手机/平板）→ ② OS + 版本（桌面端）→ ③ 浏览器 + 版本（兜底）→ "Unknown Device"。
    /// </summary>
    private static string BuildDeviceName(ClientInfo clientInfo)
    {
        var sb = new StringBuilder(48);

        // ① 品牌 + 型号（移动端通常有此信息）
        AppendPart(sb, clientInfo.Device.Brand);
        AppendPart(sb, clientInfo.Device.Model);
        if (sb.Length > 0) return sb.ToString();

        // ② 操作系统 + 版本（桌面端）
        AppendPart(sb, clientInfo.OS.Family);
        AppendVersion(sb, clientInfo.OS.Major, clientInfo.OS.Minor);
        if (sb.Length > 0) return sb.ToString();

        // ③ 浏览器名称（最终兜底）
        AppendPart(sb, clientInfo.Browser.Family);
        AppendVersion(sb, clientInfo.Browser.Major, null);
        return sb.Length > 0 ? sb.ToString() : "Unknown Device";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 设备类型检测
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 综合 UA 字符串与 UAParser 解析结果判断设备类型：Bot / Mobile / Desktop / Other。
    /// <para>检测顺序（优先级从高到低）：爬虫 → UA 关键词 → OS/Device 系列 → 已知桌面 OS → Other。</para>
    /// </summary>
    private static string DetectDeviceType(string userAgent, ClientInfo clientInfo)
    {
        // 爬虫/机器人最高优先级
        if (clientInfo.Device.IsSpider) return "Bot";

        var osFamily     = clientInfo.OS.Family     ?? string.Empty;
        var deviceFamily = clientInfo.Device.Family ?? string.Empty;

        // UA 字符串关键词（对 iOS Safari 等不完全依赖 UAParser 的情况更可靠）
        if (HasMobileSignalInUa(userAgent)) return "Mobile";

        // UAParser 的 OS / Device 系列信号
        if (ContainsAny(osFamily, MobileSignals) || ContainsAny(deviceFamily, MobileSignals))
            return "Mobile";

        // 已知桌面 OS 快速路径（Windows / macOS / Linux 等）
        if (IsKnownDesktopOs(osFamily) || ContainsAny(deviceFamily, DesktopSignals))
            return "Desktop";

        return "Other";
    }

    /// <summary>直接从 UA 字符串中检测明确的移动端信号，速度快于正则。</summary>
    private static bool HasMobileSignalInUa(string ua)
        => ua.Contains("Mobile",  StringComparison.OrdinalIgnoreCase) ||
           ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ||
           ua.Contains("iPhone",  StringComparison.OrdinalIgnoreCase) ||
           ua.Contains("iPad",    StringComparison.OrdinalIgnoreCase);

    /// <summary>通过 OS family 前缀快速识别常见桌面操作系统。</summary>
    private static bool IsKnownDesktopOs(string osFamily)
        => osFamily.StartsWith("Windows",   StringComparison.OrdinalIgnoreCase) ||
           osFamily.StartsWith("Mac",       StringComparison.OrdinalIgnoreCase) ||
           osFamily.StartsWith("Linux",     StringComparison.OrdinalIgnoreCase) ||
           osFamily.StartsWith("Chrome OS", StringComparison.OrdinalIgnoreCase) ||
           osFamily.StartsWith("Ubuntu",    StringComparison.OrdinalIgnoreCase) ||
           osFamily.StartsWith("Fedora",    StringComparison.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────────────
    // 辅助方法
    // ─────────────────────────────────────────────────────────────────────────

    private static void AppendPart(StringBuilder sb, string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Equals("Other", StringComparison.OrdinalIgnoreCase))
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
        foreach (var kw in keywords)
            if (source.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
