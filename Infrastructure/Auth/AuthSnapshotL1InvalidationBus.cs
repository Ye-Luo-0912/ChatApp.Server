using System.Globalization;
using System.Threading.Channels;
using Core.Interfaces.Auth;
using Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure.Auth;

/// <summary>
/// 通过 Redis Pub/Sub 将用户认证 fence 的本机 L1 条目驱逐到所有 API 实例。
/// 订阅器持续监督重连；消息不是可靠队列，SecurityVersion/TTL 才是最终安全边界。
/// </summary>
public sealed class AuthSnapshotL1InvalidationBus : BackgroundService, IAuthSnapshotL1InvalidationBus
{
    private const string ChannelName = "chatapp:auth:fence:l1:invalidate:v1";
    private const string MetricsBusName = "auth_fence";
    private const int PublishQueueCapacity = 1024;
    private static readonly RedisChannel Channel = RedisChannel.Literal(ChannelName);

    private readonly ISubscriber _subscriber;
    private readonly ILogger<AuthSnapshotL1InvalidationBus> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Channel<PublishNotice> _publishQueue = System.Threading.Channels.Channel.CreateBounded<PublishNotice>(
        new BoundedChannelOptions(PublishQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // Publish is deliberately non-blocking. With Wait mode,
            // TryWrite returns false when the bounded queue is full, making
            // the drop observable instead of silently discarding an item.
            FullMode = BoundedChannelFullMode.Wait,
        });
    private Action<long, long?>? _evict;

    public AuthSnapshotL1InvalidationBus(
        IConnectionMultiplexer redis,
        ILogger<AuthSnapshotL1InvalidationBus> logger)
    {
        _subscriber = redis.GetSubscriber();
        _logger = logger;
        redis.ConnectionRestored += (_, _) => SignalSupervisor();
        redis.ConnectionFailed += (_, _) => SignalSupervisor();
    }

    public void Register(Action<long, long?> evict)
        => Interlocked.Exchange(ref _evict, evict);

    public void Publish(long userId, long? minimumSecurityVersion = null)
    {
        if (userId <= 0)
            return;

        if (!_publishQueue.Writer.TryWrite(new PublishNotice(
                userId,
                minimumSecurityVersion,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())))
        {
            AuthSecurityMetrics.RecordInvalidationQueueDrop(MetricsBusName);
            _logger.LogWarning(
                "认证 fence L1 失效发布队列已满 UserId={UserId}，丢弃优化通知；版本栅栏和 TTL 仍负责最终收敛",
                userId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var publisher = PublishLoopAsync(stoppingToken);
        var retryDelay = TimeSpan.FromSeconds(1);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    AuthSecurityMetrics.SetInvalidationSubscriberConnected(MetricsBusName, false);
                    try
                    {
                        await _subscriber.UnsubscribeAsync(Channel).ConfigureAwait(false);
                    }
                    catch (RedisException)
                    {
                        // Disconnects can leave no active subscription.
                    }

                    await _subscriber.SubscribeAsync(Channel, OnMessage).ConfigureAwait(false);
                    AuthSecurityMetrics.SetInvalidationSubscriberConnected(MetricsBusName, true);
                    AuthSecurityMetrics.RecordInvalidationReconnect(MetricsBusName);
                    retryDelay = TimeSpan.FromSeconds(1);

                    await _wake.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AuthSecurityMetrics.SetInvalidationSubscriberConnected(MetricsBusName, false);
                    _logger.LogWarning(ex, "认证 fence L1 失效订阅失败，将持续重试");
                    try
                    {
                        await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    retryDelay = TimeSpan.FromSeconds(Math.Min(30, retryDelay.TotalSeconds * 2));
                }
            }
        }
        finally
        {
            AuthSecurityMetrics.SetInvalidationSubscriberConnected(MetricsBusName, false);
            _publishQueue.Writer.TryComplete();
            try
            {
                await publisher.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        AuthSecurityMetrics.SetInvalidationSubscriberConnected(MetricsBusName, false);
        try
        {
            await _subscriber.UnsubscribeAsync(Channel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "认证 fence L1 失效订阅关闭失败");
        }

        _publishQueue.Writer.TryComplete();
        SignalSupervisor();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnMessage(RedisChannel _, RedisValue value)
    {
        if (!value.HasValue)
            return;

        var payload = value.ToString();
        var firstSeparator = payload.IndexOf(':');
        var userPart = firstSeparator > 0 ? payload[..firstSeparator] : payload;
        var remainder = firstSeparator > 0 ? payload[(firstSeparator + 1)..] : string.Empty;
        var secondSeparator = remainder.IndexOf(':');
        var minimumPart = secondSeparator >= 0 ? remainder[..secondSeparator] : remainder;
        var publishedAtPart = secondSeparator >= 0 ? remainder[(secondSeparator + 1)..] : string.Empty;

        long? minimumVersion = null;
        if (long.TryParse(
                minimumPart,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedVersion)
            && parsedVersion > 0)
        {
            minimumVersion = parsedVersion;
        }

        if (secondSeparator >= 0
            && long.TryParse(
                publishedAtPart,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var publishedAt))
        {
            AuthSecurityMetrics.RecordInvalidationLag(MetricsBusName, publishedAt);
        }

        if (long.TryParse(
                userPart,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var userId)
            && userId > 0)
        {
            Volatile.Read(ref _evict)?.Invoke(userId, minimumVersion);
        }
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var notice in _publishQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var version = notice.MinimumSecurityVersion?.ToString(CultureInfo.InvariantCulture) ?? "0";
                    var timestamp = notice.PublishedAtUnixMilliseconds.ToString(CultureInfo.InvariantCulture);
                    var payload = notice.UserId.ToString(CultureInfo.InvariantCulture)
                                  + ":" + version
                                  + ":" + timestamp;
                    await _subscriber.PublishAsync(Channel, payload).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AuthSecurityMetrics.RecordInvalidationPublishFailure(MetricsBusName);
                    // Pub/Sub is only a low-latency optimization. The durable
                    // version floor and short L1 TTL remain the safety boundary.
                    _logger.LogDebug(ex, "认证 fence L1 失效广播失败");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private readonly record struct PublishNotice(
        long UserId,
        long? MinimumSecurityVersion,
        long PublishedAtUnixMilliseconds);

    private void SignalSupervisor()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending wake-up already exists.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown race; no work remains to schedule.
        }
    }
}
