using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
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
    private static readonly Meter WorkerMeter = new("Infrastructure.Workers");
    private static readonly Histogram<double> WaitHistogram =
        WorkerMeter.CreateHistogram<double>("worker.wait", "ms", "按 Worker 分类的并发槽等待时间");
    private static readonly Counter<long> Throughput =
        WorkerMeter.CreateCounter<long>("worker.throughput", "jobs", "获取并发槽并完成释放的作业数");
    private static readonly Counter<long> LeaseLost =
        WorkerMeter.CreateCounter<long>("worker.lease_lost", "jobs", "租约丢失作业数");

    private readonly SemaphoreSlim _globalSemaphore;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perWorker = new();

    private long _totalWaitTicks;
    private long _waitCount;
    private readonly ConcurrentDictionary<string, long> _oldestPendingByWorker = new(StringComparer.Ordinal);

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

    public WorkerConcurrencyManager(IOptions<WorkerConcurrencyOptions> options)
    {
        var opts = options.Value;
        _globalSemaphore = new SemaphoreSlim(
            Math.Max(1, opts.GlobalMaxConcurrency),
            Math.Max(1, opts.GlobalMaxConcurrency));
    }

    /// <summary>
    /// 获取并发槽。先获取 Worker 专属信号量，再获取全局信号量。
    /// <para>
    /// P0-5.1：顺序必须是 专属 → 全局。若先拿全局再等专属，一类 Worker 大量提交任务时会占满所有全局槽，
    /// 然后在自己的专属信号量前排队，导致其他 Worker 完全无法执行（队头阻塞）。
    /// 先拿专属槽保证调用方已获得本类 Worker 的执行权，再竞争全局槽；其他 Worker 类型不会被本类排队项阻塞。
    /// </para>
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

        var startedAt = Stopwatch.GetTimestamp();
        // 先专属：确保本类 Worker 已有执行权，避免占着全局槽空等专属槽。
        await workerSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _globalSemaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            workerSemaphore.Release();
            throw;
        }
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        Interlocked.Add(ref _totalWaitTicks, elapsed.Ticks);
        Interlocked.Increment(ref _waitCount);
        WaitHistogram.Record(elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("worker", workerName));

        return new ConcurrencyScope(_globalSemaphore, workerSemaphore, workerName);
    }

    /// <summary>
    /// 非阻塞预留一个 Worker + 全局槽位，用于在 claim 前确定真实可用并发。
    /// 返回 false 时调用方应稍后轮询，而不是先领取作业再等待槽位。
    /// </summary>
    public bool TryAcquire(
        string workerName, int workerMaxConcurrency, out IAsyncDisposable? scope)
    {
        var workerSemaphore = _perWorker.GetOrAdd(
            workerName,
            _ => new SemaphoreSlim(
                Math.Max(1, workerMaxConcurrency),
                Math.Max(1, workerMaxConcurrency)));

        if (!workerSemaphore.Wait(0))
        {
            scope = null;
            return false;
        }

        if (!_globalSemaphore.Wait(0))
        {
            workerSemaphore.Release();
            scope = null;
            return false;
        }

        WaitHistogram.Record(0,
            new KeyValuePair<string, object?>("worker", workerName));
        scope = new ConcurrencyScope(_globalSemaphore, workerSemaphore, workerName);
        return true;
    }

    /// <summary>记录最老待处理任务的时间戳（供 oldest-job-age 指标）。</summary>
    public void RecordOldestPendingJob(DateTimeOffset? oldestJobAt)
        => RecordOldestPendingJob("notification", oldestJobAt);

    public void RecordOldestPendingJob(string workerName, DateTimeOffset? oldestJobAt)
    {
        if (oldestJobAt is { } at)
        {
            _oldestPendingByWorker[workerName] = at.UtcTicks;
        }
        else
        {
            _oldestPendingByWorker.TryRemove(workerName, out _);
        }
    }

    public IEnumerable<Measurement<double>> GetOldestPendingJobMeasurements()
    {
        var now = DateTimeOffset.UtcNow.Ticks;
        foreach (var pair in _oldestPendingByWorker)
        {
            var ageMs = Math.Max(0, TimeSpan.FromTicks(now - pair.Value).TotalMilliseconds);
            yield return new Measurement<double>(
                ageMs,
                new KeyValuePair<string, object?>("worker", pair.Key));
        }
    }

    public void RecordLeaseLost(string workerName)
        => LeaseLost.Add(1, new KeyValuePair<string, object?>("worker", workerName));

    public void Dispose()
    {
        _globalSemaphore.Dispose();
        foreach (var sem in _perWorker.Values)
            sem.Dispose();
    }

    private sealed class ConcurrencyScope(
        SemaphoreSlim global,
        SemaphoreSlim worker,
        string workerName) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            worker.Release();
            global.Release();
            Throughput.Add(1, new KeyValuePair<string, object?>("worker", workerName));
            return ValueTask.CompletedTask;
        }
    }
}
