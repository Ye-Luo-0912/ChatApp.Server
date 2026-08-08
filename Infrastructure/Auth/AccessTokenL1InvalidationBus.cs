using System.Globalization;
using System.Threading.Channels;
using Core.Interfaces;
using Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure.Auth;

/// <summary>通过 Redis Pub/Sub 将访问令牌 L1 驱逐广播到所有 API 实例。</summary>
public sealed class AccessTokenL1InvalidationBus : BackgroundService, IAccessTokenL1InvalidationBus
{
    private const string ChannelName = "chatapp:auth:l1:invalidate:v1";
    private const string MetricsBusName = "access_token";
    private const int PublishQueueCapacity = 1024;
    private static readonly RedisChannel Channel = RedisChannel.Literal(ChannelName);
    private readonly ISubscriber _subscriber;
    private readonly ILogger<AccessTokenL1InvalidationBus> _logger;
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
    private Action<string>? _evict;

    public AccessTokenL1InvalidationBus(
        IConnectionMultiplexer redis,
        ILogger<AccessTokenL1InvalidationBus> logger)
    {
        _subscriber = redis.GetSubscriber();
        _logger = logger;
        redis.ConnectionRestored += (_, _) => SignalSupervisor();
        redis.ConnectionFailed += (_, _) => SignalSupervisor();
    }

    public void Register(Action<string> evict)
        => Interlocked.Exchange(ref _evict, evict);

    public void Publish(string accessTokenKey)
    {
        if (string.IsNullOrWhiteSpace(accessTokenKey))
            return;

        if (!_publishQueue.Writer.TryWrite(new PublishNotice(
                accessTokenKey,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())))
        {
            AuthSecurityMetrics.RecordInvalidationQueueDrop(MetricsBusName);
            _logger.LogWarning(
                "访问令牌 L1 失效发布队列已满，丢弃优化通知；本机驱逐和 TTL 仍负责最终收敛");
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
                    // Re-subscribe is cheap and recovers from both an initial
                    // connection race and a Redis Pub/Sub reconnect.
                    try
                    {
                        await _subscriber.UnsubscribeAsync(Channel).ConfigureAwait(false);
                    }
                    catch (RedisException)
                    {
                        // There may be no active subscription after a disconnect.
                    }

                    await _subscriber.SubscribeAsync(Channel, OnMessage).ConfigureAwait(false);
                    AuthSecurityMetrics.SetInvalidationSubscriberConnected(MetricsBusName, true);
                    AuthSecurityMetrics.RecordInvalidationReconnect(MetricsBusName);
                    retryDelay = TimeSpan.FromSeconds(1);

                    // Periodically probe the subscription and wake immediately
                    // after StackExchange.Redis reports a connection change.
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
                    _logger.LogWarning(ex, "访问令牌 L1 失效订阅失败，将持续重试");
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
            _logger.LogDebug(ex, "访问令牌 L1 失效订阅关闭失败");
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
        var separator = payload.LastIndexOf('|');
        var key = separator > 0 ? payload[..separator] : payload;
        if (separator > 0
            && long.TryParse(
                payload[(separator + 1)..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var publishedAt))
        {
            AuthSecurityMetrics.RecordInvalidationLag(MetricsBusName, publishedAt);
        }

        Volatile.Read(ref _evict)?.Invoke(key);
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var notice in _publishQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var payload = notice.Key + "|" + notice.PublishedAtUnixMilliseconds.ToString(
                        CultureInfo.InvariantCulture);
                    await _subscriber.PublishAsync(Channel, payload).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AuthSecurityMetrics.RecordInvalidationPublishFailure(MetricsBusName);
                    // Pub/Sub is an optimization path. Local eviction and the
                    // durable token/fence state remain the correctness boundary.
                    _logger.LogDebug(ex, "访问令牌 L1 失效广播失败");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private readonly record struct PublishNotice(string Key, long PublishedAtUnixMilliseconds);

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
