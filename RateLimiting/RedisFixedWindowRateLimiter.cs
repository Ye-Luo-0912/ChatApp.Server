using System.Threading.RateLimiting;
using Core.Interfaces.Cache;

namespace ChatApp.Server.RateLimiting;

/// <summary>基于 Redis INCR 的固定窗口限流，跨实例共享额度。</summary>
public sealed class RedisFixedWindowRateLimiter : RateLimiter
{
    private readonly ICacheProvider _cache;
    private readonly string _partitionKey;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private int _disposed;

    public RedisFixedWindowRateLimiter(
        ICacheProvider cache,
        string partitionKey,
        int permitLimit,
        TimeSpan window)
    {
        _cache = cache;
        _partitionKey = partitionKey;
        _permitLimit = Math.Max(1, permitLimit);
        _window = window <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : window;
    }

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
        => AcquireAsyncCore(permitCount, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount, CancellationToken cancellationToken)
    {
        if (permitCount > _permitLimit)
            return new RedisLease(false);

        var key = "rl:fw:" + _partitionKey;
        var count = await _cache.StringIncrementAsync(key, _window, cancellationToken).ConfigureAwait(false);
        if (count > _permitLimit)
            return new RedisLease(false);

        return new RedisLease(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        base.Dispose(disposing);
    }

    private sealed class RedisLease(bool isAcquired) : RateLimitLease
    {
        public override bool IsAcquired { get; } = isAcquired;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
