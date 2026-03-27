using System.Net;
using System.Text.Json;
using Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public class GeoLocationService : IGeoLocationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GeoLocationService> _logger;

    public GeoLocationService(HttpClient httpClient, ILogger<GeoLocationService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string?> GetLocationAsync(string? clientIp, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(clientIp) || !IsValidIp(clientIp))
            {
                _logger.LogWarning("Invalid IP: {IP}", clientIp);
                return "未知";
            }

            // 调用免费 API
            var response = await _httpClient.GetAsync($"json/{clientIp}?fields=status,country,city", cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            // 直接解析 JSON 字符串
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 检查 API 响应状态
            if (root.GetProperty("status").GetString() != "success")
            {
                _logger.LogError("API error: {Response}", json);
                return "未知";
            }

            // 提取国家城市信息
            var country = root.GetProperty("country").GetString()?.Trim();
            var city = root.GetProperty("city").GetString()?.Trim();

            // 组合结果（任意字段为空则返回未知）
            return string.IsNullOrEmpty(country) || string.IsNullOrEmpty(city)
                ? "未知"
                : $"{country}>{city}";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException)
        {
            _logger.LogError(ex, "Geolocation failed for IP: {IP}", clientIp);
            return "未知";
        }
    }

    private static bool IsValidIp(string ip)
    {
        return IPAddress.TryParse(ip, out _) &&
               ip != "::1" && // 过滤本地 IPv6
               !ip.StartsWith("127.");
        // 过滤本地 IPv4
    }
}