using Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure.Auth;

/// <summary>通过 Redis Pub/Sub 将访问令牌 L1 驱逐广播到所有 API 实例。</summary>
public sealed class AccessTokenL1InvalidationBus(
    IConnectionMultiplexer redis,
    ILogger<AccessTokenL1InvalidationBus> logger) : IAccessTokenL1InvalidationBus, IHostedService
{
    private const string ChannelName = "chatapp:auth:l1:invalidate:v1";
    private static readonly RedisChannel Channel = RedisChannel.Literal(ChannelName);
    private readonly ISubscriber _subscriber = redis.GetSubscriber();
    private Action<string>? _evict;

    public void Register(Action<string> evict)
        => Interlocked.Exchange(ref _evict, evict);

    public void Publish(string accessTokenKey)
    {
        if (string.IsNullOrWhiteSpace(accessTokenKey))
            return;
        _ = PublishCoreAsync(accessTokenKey);
    }

    private async Task PublishCoreAsync(string accessTokenKey)
    {
        try
        {
            await _subscriber.PublishAsync(Channel, accessTokenKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // L1 广播是优化路径，不能让撤销结果依赖 Pub/Sub；本机驱逐和 TTL 仍然生效。
            logger.LogDebug(ex, "访问令牌 L1 失效广播失败");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _subscriber.SubscribeAsync(Channel, (channel, value) =>
            {
                if (value.HasValue)
                    Volatile.Read(ref _evict)?.Invoke(value.ToString());
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "访问令牌 L1 失效订阅启动失败，将由 TTL 保证最终收敛");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _subscriber.UnsubscribeAsync(Channel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "访问令牌 L1 失效订阅关闭失败");
        }
    }
}
