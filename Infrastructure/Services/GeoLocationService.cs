using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Core.Exceptions;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public class GeoLocationService : IGeoLocationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeoLocationService> _logger;
    private readonly ICacheValueStore _cache;

    private const string HttpClientName = nameof(GeoLocationService);
    private const string CacheKeyPrefix = "geo:ip:";
    private static readonly TimeSpan SuccessCacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan UnknownCacheTtl = TimeSpan.FromHours(1);
    private const int MaxRetries = 3;

    public GeoLocationService(IHttpClientFactory httpClientFactory, ILogger<GeoLocationService> logger, ICacheValueStore cache)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cache = cache;
    }

    public async Task<string?> GetLocationAsync(string? clientIp, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientIp) || !IsValidPublicIp(clientIp))
        {
            _logger.LogDebug("Private or invalid IP skipped: {IP}", clientIp);
            return "未知";
        }

        // 优先读缓存，避免重复调用外部 API
        var cacheKey = CacheKeyPrefix + clientIp;
        try
        {
            var cached = await _cache.StringGetAsync(cacheKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (cached is not null)
                return cached;
        }
        catch (CacheUnavailableException ex)
        {
            _logger.LogWarning(ex, "Cache unavailable, falling through to API for IP: {IP}", clientIp);
        }

        var result = await FetchWithRetryAsync(clientIp, cancellationToken).ConfigureAwait(false);

        // 网络异常时 result 为 null，不缓存，留待下次重试
        if (result is not null)
        {
            try
            {
                var ttl = result == "未知" ? UnknownCacheTtl : SuccessCacheTtl;
                await _cache.StringSetAsync(cacheKey, result, ttl, cancellationToken).ConfigureAwait(false);
            }
            catch (CacheUnavailableException ex)
            {
                _logger.LogWarning(ex, "Failed to cache geolocation result for IP: {IP}", clientIp);
            }
        }

        return result ?? "未知";
    }

    /// <summary>
    /// 带指数退避重试的 HTTP 请求，仅对网络层异常重试。
    /// </summary>
    private async Task<string?> FetchWithRetryAsync(string clientIp, CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient(HttpClientName);

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var response = await httpClient
                    .GetAsync($"json/{clientIp}?fields=status,country,city", cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Geolocation API returned HTTP {StatusCode} for IP {IP}",
                        (int)response.StatusCode, clientIp);
                    return "未知"; // HTTP 错误不重试
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseLocation(json, clientIp);
            }
            catch (OperationCanceledException)
            {
                throw; // 尊重取消信号，不重试
            }
            catch (HttpRequestException ex)
            {
                if (attempt == MaxRetries)
                {
                    _logger.LogError(ex, "Geolocation failed after {MaxRetries} attempts for IP: {IP}",
                        MaxRetries, clientIp);
                    return null; // 返回 null 表示网络故障，跳过缓存
                }

                var delay = TimeSpan.FromMilliseconds(150 * attempt);
                _logger.LogWarning(ex,
                    "Geolocation attempt {Attempt}/{MaxRetries} failed for IP {IP}, retrying in {DelayMs}ms",
                    attempt, MaxRetries, clientIp, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>
    /// 解析 ip-api.com 响应 JSON，使用 TryGetProperty 避免异常控制流。
    /// </summary>
    private string ParseLocation(string json, string clientIp)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var statusProp) ||
                statusProp.GetString() != "success")
            {
                _logger.LogWarning("Geolocation API returned non-success for IP {IP}: {Response}",
                    clientIp, json);
                return "未知";
            }

            var country = root.TryGetProperty("country", out var cp) ? cp.GetString()?.Trim() : null;
            var city    = root.TryGetProperty("city",    out var ci) ? ci.GetString()?.Trim() : null;

            return string.IsNullOrEmpty(country) || string.IsNullOrEmpty(city)
                ? "未知"
                : $"{country}>{city}";
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse geolocation response for IP: {IP}", clientIp);
            return "未知";
        }
    }

    /// <summary>
    /// 过滤私有地址、回环、链路本地等非公网 IP。
    /// 覆盖 RFC 1918、RFC 3927、RFC 4193、RFC 6598 及 IPv6 保留段。
    /// </summary>
    private static bool IsValidPublicIp(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address))
            return false;

        // 将 IPv4-mapped IPv6（如 ::ffff:192.168.1.1）还原为 IPv4 处理
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork   => IsPublicIPv4(address),
            AddressFamily.InterNetworkV6 => IsPublicIPv6(address),
            _                            => false
        };
    }

    private static bool IsPublicIPv4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return !(
            b[0] == 0                                          || // 0.0.0.0/8
            b[0] == 10                                         || // 10.0.0.0/8        RFC 1918
            b[0] == 100 && b[1] is >= 64 and <= 127           || // 100.64.0.0/10     RFC 6598 共享地址
            b[0] == 127                                        || // 127.0.0.0/8       回环
            b[0] == 169 && b[1] == 254                        || // 169.254.0.0/16    链路本地
            b[0] == 172 && b[1] is >= 16 and <= 31            || // 172.16.0.0/12     RFC 1918
            b[0] == 192 && b[1] == 168                        || // 192.168.0.0/16    RFC 1918
            b[0] == 198 && b[1] is 18 or 19                   || // 198.18.0.0/15     基准测试用
            b[0] == 255                                           // 255.255.255.255   广播
        );
    }

    private static bool IsPublicIPv6(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;  // ::1
        if (address.IsIPv6LinkLocal)       return false;  // fe80::/10
        if (address.IsIPv6SiteLocal)       return false;  // fec0::/10（已废弃但仍需过滤）

        // fc00::/7  唯一本地地址（Unique Local，类似 RFC 1918）
        var b = address.GetAddressBytes();
        if ((b[0] & 0xFE) == 0xFC)         return false;

        return true;
    }
}
