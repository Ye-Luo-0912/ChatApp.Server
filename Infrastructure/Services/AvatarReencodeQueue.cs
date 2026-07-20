using System.Diagnostics;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>有界头像重编码闸门：可配置并发，并暴露等待/深度/拒绝指标。</summary>
public sealed class AvatarReencodeQueue
{
    private readonly SemaphoreSlim _workers;
    private readonly AvatarReencodeMetrics _metrics;
    private readonly int _acquireTimeoutMs;

    public AvatarReencodeQueue(
        IOptions<AvatarStorageOptions> options,
        AvatarReencodeMetrics metrics)
    {
        var opts = options.Value;
        var n = Math.Max(1, opts.ReencodeMaxConcurrency);
        _workers = new SemaphoreSlim(n, n);
        _metrics = metrics;
        _acquireTimeoutMs = Math.Max(0, opts.ReencodeAcquireTimeoutMilliseconds);
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        _metrics.BeginWait();
        var waitSw = Stopwatch.StartNew();
        try
        {
            bool acquired;
            if (_acquireTimeoutMs <= 0)
            {
                await _workers.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;
            }
            else
            {
                acquired = await _workers.WaitAsync(_acquireTimeoutMs, cancellationToken).ConfigureAwait(false);
            }

            if (!acquired)
            {
                _metrics.RecordRejected();
                throw new TimeoutException("头像重编码队列繁忙，请稍后重试");
            }
        }
        catch
        {
            _metrics.EndWait(waitSw.Elapsed.TotalMilliseconds);
            throw;
        }

        _metrics.EndWait(waitSw.Elapsed.TotalMilliseconds);
        _metrics.BeginWork();
        var workSw = Stopwatch.StartNew();
        try
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _metrics.EndWork(workSw.Elapsed.TotalMilliseconds);
            _workers.Release();
        }
    }
}
