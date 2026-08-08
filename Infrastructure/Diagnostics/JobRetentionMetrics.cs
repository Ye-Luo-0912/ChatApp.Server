using System.Diagnostics.Metrics;

namespace Infrastructure.Diagnostics;

/// <summary>统一 durable-job retention 的删除和失败计数。</summary>
public static class JobRetentionMetrics
{
    private static readonly Meter Meter = new("Infrastructure.JobRetention");
    private static readonly Counter<long> Deleted = Meter.CreateCounter<long>(
        "job.retention.deleted", "jobs", "按队列删除的完成/死信作业数");
    private static readonly Counter<long> Failed = Meter.CreateCounter<long>(
        "job.retention.failed", "runs", "retention 批次失败次数");

    public static void RecordDeleted(string queue, int count)
    {
        if (count > 0)
            Deleted.Add(count, new KeyValuePair<string, object?>("queue", queue));
    }

    public static void RecordFailure(string queue)
        => Failed.Add(1, new KeyValuePair<string, object?>("queue", queue));
}
