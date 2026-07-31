using System.Threading.Channels;
using Core.Interfaces;
using Core.Models.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class LoginRiskAnalyzer(
    IServiceScopeFactory scopeFactory,
    ILogger<LoginRiskAnalyzer> logger) : BackgroundService, ILoginRiskAnalyzer
{
    private readonly Channel<LoginRiskWorkItem> _channel =
        Channel.CreateBounded<LoginRiskWorkItem>(new BoundedChannelOptions(2_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public void Enqueue(LoginRiskWorkItem item)
    {
        if (!_channel.Writer.TryWrite(item))
            logger.LogDebug("登录风险队列已满，丢弃最旧项");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await AnalyzeAsync(item, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "登录风险分析失败 UserId={UserId}", item.UserId);
            }
        }
    }

    private async Task AnalyzeAsync(LoginRiskWorkItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.ClientIp))
            return;

        if (item.IpChanged)
            AuthSecurityMetrics.RecordRisk("ip_changed");

        await using var scope = scopeFactory.CreateAsyncScope();
        var geo = scope.ServiceProvider.GetRequiredService<IGeoLocationService>();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var notify = scope.ServiceProvider.GetRequiredService<ISecurityNotificationService>();

        var location = await geo.GetLocationAsync(item.ClientIp, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(location) || location == "未知")
            return;

        // 取该用户近期成功登录的地理位置（非本次），比较国家/城市片段
        var recentLocations = await db.SecurityEvents.AsNoTracking()
            .Where(e => e.UserId == item.UserId
                        && e.EventType == SecurityEventType.LoginSuccess
                        && (item.SessionId == null || e.SessionId != item.SessionId)
                        && e.Location != null
                        && e.Location != ""
                        && e.Location != "未知")
            .OrderByDescending(e => e.Id)
            .Select(e => e.Location!)
            .Take(10)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // 回填本次登录成功事件的 Location
        await db.SecurityEvents
            .Where(e => e.UserId == item.UserId
                        && e.EventType == SecurityEventType.LoginSuccess
                        && e.SessionId == item.SessionId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.Location, location),
                cancellationToken)
            .ConfigureAwait(false);

        if (recentLocations.Count == 0)
            return;

        var currentParts = SplitGeo(location);
        var matched = recentLocations.Any(prev => GeoCompatible(SplitGeo(prev), currentParts));
        if (matched)
            return;

        // 可信设备：DeviceIdHint 仅作抑制误报的软信号（非鉴权）
        if (!string.IsNullOrWhiteSpace(item.DeviceId))
        {
            var now = DateTimeOffset.UtcNow;
            var trusted = await db.TrustedDevices.AsNoTracking()
                .AnyAsync(
                    d => d.UserId == item.UserId
                         && d.RevokedAt == null
                         && d.ExpiresAt > now
                         && d.DeviceIdHint == item.DeviceId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (trusted)
            {
                logger.LogDebug(
                    "地理不兼容但设备可信，跳过异常地点通知 UserId={UserId} DeviceId={DeviceId}",
                    item.UserId, item.DeviceId);
                AuthSecurityMetrics.RecordRisk("unusual_location_suppressed_trusted");
                return;
            }
        }

        // 同会话幂等：避免与热路径或其他分析重复通知
        var sessionKey = string.IsNullOrWhiteSpace(item.SessionId)
            ? null
            : $"{item.UserId}:LoginUnusualLocation:{item.SessionId}";

        var alreadyRecorded = !string.IsNullOrWhiteSpace(item.SessionId)
            && await db.SecurityEvents.AsNoTracking()
            .AnyAsync(
                    e => e.UserId == item.UserId
                         && e.EventType == SecurityEventType.LoginUnusualLocation
                         && e.SessionId == item.SessionId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (alreadyRecorded)
            return;

        db.SecurityEvents.Add(new SecurityEvent
        {
            UserId = item.UserId,
            EventType = SecurityEventType.LoginUnusualLocation,
            DeviceId = item.DeviceId,
            SessionId = item.SessionId,
            ClientIp = item.ClientIp,
            Location = location,
            Detail = $"async-geo loc={location}; ipChanged={item.IpChanged}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await notify.NotifyAsync(
            item.UserId,
            "LoginUnusualLocation",
            "异常地点登录",
            $"检测到与常用地区不一致的登录位置：{location}（IP 网段：{IpPrivacy.Display(item.ClientIp)}）。",
            preferEmail: true,
            cancellationToken,
            sessionKey).ConfigureAwait(false);

        AuthSecurityMetrics.RecordRisk("unusual_location");
        logger.LogInformation(
            "异步地理风险命中 UserId={UserId} Location={Location} IpChanged={IpChanged}",
            item.UserId, location, item.IpChanged);
    }

    private static string[] SplitGeo(string location)
        => location.Split([',', '/', '|', ' ', '>'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool GeoCompatible(string[] previous, string[] current)
    {
        if (previous.Length == 0 || current.Length == 0)
            return true;
        // 任一侧出现相同的国家或城市片段即视为兼容（降低误报）
        return previous.Intersect(current, StringComparer.OrdinalIgnoreCase).Any();
    }
}
