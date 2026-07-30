using System.Collections.Concurrent;
using System.Diagnostics;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>
/// 全局后台 Worker 并发管理器。
/// <para>
/// 提供：
/// <list type="bullet">
///   <item>全局并发预算：限制所有 Worker 同时执行的任务总数</item>
///   <item>每类 Worker 独立并发配置</item>
///   <item>pool wait 指标：等待获取并发槽的时间</item>
///   <item>oldest-job-age 指标：最老待处理任务的年龄</item>
/// </list>
/// </para>
/// </summary>
public sealed class WorkerConcurrencyManager : IDisposable
{
    private readonly SemaphoreSlim _globalSemaphore;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perWorker = new();

    private long _totalWaitTicks;
    private long _waitCount;
    private long _oldestPendingJobTicks;

    /// <summary>平均等待获取并发槽的时间。</summary>
    public TimeSpan AverageWaitTime
    {
        get
        {
            var count = Interlocked.Read(ref _waitCount);
            if (count == 0) return TimeSpan.Zero;
            return TimeSpan.FromTicks(Interlocked.Read(ref _totalWaitTicks) / count);
        }
    }

    /// <summary>最老待处理任务的年龄。</summary>
    public TimeSpan OldestPendingJobAge
    {
        get
        {
            var ticks = Interlocked.Read(ref _oldestPendingJobTicks);
            if (ticks == 0) return TimeSpan.Zero;
            return TimeSpan.FromTicks(DateTimeOffset.UtcNow.Ticks - ticks);
        }
    }

    public WorkerConcurrencyManager(IOptions<WorkerConcurrencyOptions> options)
    {
        var opts = options.Value;
        _globalSemaphore = new SemaphoreSlim(
            Math.Max(1, opts.GlobalMaxConcurrency),
            Math.Max(1, opts.GlobalMaxConcurrency));
    }

    /// <summary>
    /// 获取并发槽。先获取全局信号量，再获取 Worker 专属信号量。
    /// 返回的 <see cref="IAsyncDisposable"/> 释放时归还两个信号量。
    /// </summary>
    public async Task<IAsyncDisposable> AcquireAsync(
        string workerName, int workerMaxConcurrency, CancellationToken ct)
    {
        var workerSemaphore = _perWorker.GetOrAdd(
            workerName,
            _ => new SemaphoreSlim(
                Math.Max(1, workerMaxConcurrency),
                Math.Max(1, workerMaxConcurrency)));

        var sw = Stopwatch.StartNew();
        await _globalSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await workerSemaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _globalSemaphore.Release();
            throw;
        }
        sw.Stop();

        Interlocked.Add(ref _totalWaitTicks, sw.ElapsedTicks);
        Interlocked.Increment(ref _waitCount);

        return new ConcurrencyScope(_globalSemaphore, workerSemaphore);
    }

    /// <summary>记录最老待处理任务的时间戳（供 oldest-job-age 指标）。</summary>
    public void RecordOldestPendingJob(DateTimeOffset? oldestJobAt)
    {
        if (oldestJobAt is { } at)
            Interlocked.Exchange(ref _oldestPendingJobTicks, at.UtcTicks);
        else
            Interlocked.Exchange(ref _oldestPendingJobTicks, 0);
    }

    public void Dispose()
    {
        _globalSemaphore.Dispose();
        foreach (var sem in _perWorker.Values)
            sem.Dispose();
    }

    private sealed class ConcurrencyScope(SemaphoreSlim global, SemaphoreSlim worker) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            worker.Release();
            global.Release();
            return ValueTask.CompletedTask;
        }
    }
}
