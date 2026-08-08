using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public class GeoLocationService : IGeoLocationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeoLocationService> _logger;
    private readonly IDerivedCache _cache;
    private readonly LocalGeoLocationDatabase _localDatabase;
    private readonly GeoLocationOptions _geoOptions;
    private readonly byte[] _cacheKeySecret;

    private const string HttpClientName = nameof(GeoLocationService);
    private const string CacheKeyPrefix = "geo:ip:";
    private static readonly TimeSpan SuccessCacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan UnknownCacheTtl = TimeSpan.FromHours(1);
    private const int MaxRetries = 3;

    public GeoLocationService(
        IHttpClientFactory httpClientFactory,
        ILogger<GeoLocationService> logger,
        IDerivedCache cache,
        IOptions<SecurityOptions> securityOptions,
        IOptions<GeoLocationOptions> geoOptions,
        LocalGeoLocationDatabase localDatabase)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cache = cache;
        _geoOptions = geoOptions.Value;
        _localDatabase = localDatabase;
        var secret = securityOptions.Value.SecretEncryptionKey;
        _cacheKeySecret = string.IsNullOrWhiteSpace(secret)
            ? SHA256.HashData(Encoding.UTF8.GetBytes("ChatApp.GeoLocation.CacheKey.v1"))
            : SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    public async Task<string?> GetLocationAsync(string? clientIp, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientIp) || !IsValidPublicIp(clientIp))
        {
            _logger.LogDebug("Private or invalid IP skipped: {IpRef}", SafeIpReference(clientIp));
            return "未知";
        }

        // Prefer the privacy-preserving local source before consulting any
        // derived cache or external provider.
        if (_localDatabase.TryGetLocation(clientIp, out var localLocation)
            && !string.IsNullOrWhiteSpace(localLocation))
        {
            return localLocation;
        }

        // 优先读缓存，避免重复调用外部 API（IDerivedCache 内置 fail-open，连接失败视为未命中）
        // 不把原始 IP 写入 Redis key；使用服务端密钥 HMAC，避免日志/缓存泄露可回溯的完整地址。
        var cacheKey = CacheKeyPrefix + ComputeIpReference(clientIp);
        var cached = await _cache.TryGetAsync<string>(cacheKey, cancellationToken).ConfigureAwait(false);
        if (cached.Found && cached.Value is not null)
            return cached.Value;

        if (!_geoOptions.AllowExternalFallback)
            return "未知";

        var result = await FetchWithRetryAsync(clientIp, cancellationToken).ConfigureAwait(false);

        // 网络异常时 result 为 null，不缓存，留待下次重试
        if (result is not null)
        {
            var ttl = result == "未知" ? UnknownCacheTtl : SuccessCacheTtl;
            await _cache.SetAsync(cacheKey, result, ttl, cancellationToken).ConfigureAwait(false);
        }

        return result ?? "未知";
    }

    /// <summary>
    /// 对网络、超时、429 和 5xx 做有界重试；瞬时故障不写入负缓存。
    /// </summary>
    private async Task<string?> FetchWithRetryAsync(string clientIp, CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient(HttpClientName);

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var response = await httpClient
                    .GetAsync($"json/{clientIp}?fields=status,country,city", cancellationToken)
                    .ConfigureAwait(false);

                // Provider throttling and server failures are transient. Do
                // not turn them into a one-hour "未知" cache entry: doing so
                // hides recovery and makes a provider incident persist in the
                // risk pipeline long after the outage ends.
                if ((int)response.StatusCode == 429
                    || (int)response.StatusCode >= 500)
                {
                    _logger.LogWarning(
                        "Geolocation provider returned transient HTTP {StatusCode} for IpRef {IpRef}",
                        (int)response.StatusCode,
                        SafeIpReference(clientIp));
                    if (attempt < MaxRetries)
                    {
                        await DelayBeforeRetryAsync(attempt, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Geolocation API returned HTTP {StatusCode} for IpRef {IpRef}",
                        (int)response.StatusCode, SafeIpReference(clientIp));
                    return "未知"; // non-transient provider rejection
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseLocation(json, clientIp);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // caller cancellation is never retried
            }
            catch (OperationCanceledException ex)
            {
                // HttpClient.Timeout is surfaced as TaskCanceledException/
                // OperationCanceledException without the caller token being
                // cancelled. Treat that as a transient provider timeout so it
                // follows the same bounded retry/no-cache path as a network
                // failure.
                if (attempt == MaxRetries)
                {
                    _logger.LogError(
                        ex,
                        "Geolocation timed out after {MaxRetries} attempts for IpRef {IpRef}",
                        MaxRetries,
                        SafeIpReference(clientIp));
                    return null;
                }

                _logger.LogWarning(
                    ex,
                    "Geolocation timeout attempt {Attempt}/{MaxRetries} for IpRef {IpRef}",
                    attempt,
                    MaxRetries,
                    SafeIpReference(clientIp));
                await DelayBeforeRetryAsync(attempt, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == MaxRetries)
                {
                    _logger.LogError(ex, "Geolocation failed after {MaxRetries} attempts for IpRef {IpRef}",
                        MaxRetries, SafeIpReference(clientIp));
                    return null; // 返回 null 表示网络故障，跳过缓存
                }

                _logger.LogWarning(ex,
                    "Geolocation attempt {Attempt}/{MaxRetries} failed for IpRef {IpRef}, retrying",
                    attempt, MaxRetries, SafeIpReference(clientIp));
                await DelayBeforeRetryAsync(attempt, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return null;
    }

    private static Task DelayBeforeRetryAsync(
        int attempt,
        CancellationToken cancellationToken)
        => Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);

    /// <summary>
    /// 解析 GeoIP 响应 JSON，使用 TryGetProperty 避免异常控制流。
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
                _logger.LogWarning("Geolocation API returned non-success for IpRef {IpRef}",
                    SafeIpReference(clientIp));
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
            _logger.LogError(ex, "Failed to parse geolocation response for IpRef {IpRef}", SafeIpReference(clientIp));
            return "未知";
        }
    }

    private string ComputeIpReference(string ip)
        => Convert.ToHexString(HMACSHA256.HashData(_cacheKeySecret, Encoding.UTF8.GetBytes(ip)))[..32];

    private string SafeIpReference(string? ip)
        => string.IsNullOrWhiteSpace(ip) ? "none" : ComputeIpReference(ip);

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
