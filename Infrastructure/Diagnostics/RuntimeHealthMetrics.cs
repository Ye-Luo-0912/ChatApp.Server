using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure.Diagnostics;

/// <summary>进程内存、GC、Redis PING 延迟与后台 Worker 并发指标。</summary>
public sealed class RuntimeHealthMetrics : IHostedService, IDisposable
{
    private static readonly Meter Meter = new("Infrastructure.Runtime");
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RuntimeHealthMetrics> _logger;
    private readonly WorkerConcurrencyManager _concurrencyManager;
    private readonly ObservableGauge<long> _workingSet;
    private readonly ObservableGauge<long> _gcHeap;
    private readonly Histogram<double> _redisPingMs;
    private Timer? _timer;

    public RuntimeHealthMetrics(
        IConnectionMultiplexer redis,
        WorkerConcurrencyManager concurrencyManager,
        ILogger<RuntimeHealthMetrics> logger)
    {
        _redis = redis;
        _concurrencyManager = concurrencyManager;
        _logger = logger;
        _workingSet = Meter.CreateObservableGauge(
            "process.memory.working_set",
            () => Environment.WorkingSet,
            "bytes",
            "进程工作集");
        _gcHeap = Meter.CreateObservableGauge(
            "process.memory.gc_heap",
            () => GC.GetTotalMemory(false),
            "bytes",
            "GC 堆大小");
        _ = Meter.CreateObservableGauge(
            "process.gc.collections.gen0",
            () => GC.CollectionCount(0),
            "{collections}",
            "Gen0 GC 次数");
        _ = Meter.CreateObservableGauge(
            "process.gc.collections.gen1",
            () => GC.CollectionCount(1),
            "{collections}",
            "Gen1 GC 次数");
        _ = Meter.CreateObservableGauge(
            "process.gc.collections.gen2",
            () => GC.CollectionCount(2),
            "{collections}",
            "Gen2 GC 次数");
        _ = Meter.CreateObservableGauge(
            "worker.concurrency.avg_wait_ms",
            () => _concurrencyManager.AverageWaitTime.TotalMilliseconds,
            "ms",
            "后台 Worker 平均等待获取并发槽的时间");
        _ = Meter.CreateObservableGauge(
            "worker.oldest_job_age_ms",
            () => _concurrencyManager.OldestPendingJobAge.TotalMilliseconds,
            "ms",
            "最老待处理任务的年龄");
        _ = Meter.CreateObservableGauge(
            "worker.oldest_job_age_ms.by_worker",
            () => _concurrencyManager.GetOldestPendingJobMeasurements(),
            "ms",
            "按 Worker 分类的最老待处理任务年龄");
        _redisPingMs = Meter.CreateHistogram<double>("redis.ping.duration", "ms", "Redis PING 延迟");
        // 保留引用，避免被优化掉
        _ = _workingSet;
        _ = _gcHeap;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(async _ => await PingRedisAsync().ConfigureAwait(false),
            null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private async Task PingRedisAsync()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await _redis.GetDatabase().PingAsync().ConfigureAwait(false);
            sw.Stop();
            _redisPingMs.Record(sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Redis PING 失败");
        }
    }

    public void Dispose() => _timer?.Dispose();
}
