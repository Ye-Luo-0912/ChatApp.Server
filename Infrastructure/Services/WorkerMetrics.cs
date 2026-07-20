using System.Diagnostics.Metrics;

namespace Infrastructure.Services;

public sealed class NotificationOutboxMetrics
{
    private static readonly Meter Meter = new("Infrastructure.Notification.Outbox");

    private readonly Counter<long> _claimed;
    private readonly Counter<long> _sent;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _dead;
    private long _backlog;

    public NotificationOutboxMetrics()
    {
        _claimed = Meter.CreateCounter<long>("notification.outbox.claimed", "messages", "领取待处理通知次数");
        _sent = Meter.CreateCounter<long>("notification.outbox.sent", "messages", "通知投递成功次数");
        _failed = Meter.CreateCounter<long>("notification.outbox.failed", "messages", "通知投递失败次数");
        _dead = Meter.CreateCounter<long>("notification.outbox.dead", "messages", "通知进入死信次数");
        Meter.CreateObservableGauge(
            "notification.outbox.backlog",
            () => Volatile.Read(ref _backlog),
            "messages",
            "待处理/失败且到期的通知积压");
    }

    public void RecordClaimed(int count)
    {
        if (count > 0) _claimed.Add(count);
    }

    public void RecordSent() => _sent.Add(1);
    public void RecordFailed() => _failed.Add(1);
    public void RecordDead() => _dead.Add(1);

    public void SetBacklog(long count) => Volatile.Write(ref _backlog, Math.Max(0, count));
}

public sealed class AvatarReencodeMetrics
{
    private static readonly Meter Meter = new("Infrastructure.Avatar.Reencode");

    private readonly Histogram<double> _waitMs;
    private readonly Histogram<double> _workMs;
    private readonly Counter<long> _rejected;
    private long _queued;
    private long _inFlight;

    public AvatarReencodeMetrics()
    {
        _waitMs = Meter.CreateHistogram<double>("avatar.reencode.wait", "ms", "等待获取重编码闸门耗时");
        _workMs = Meter.CreateHistogram<double>("avatar.reencode.duration", "ms", "重编码执行耗时");
        _rejected = Meter.CreateCounter<long>("avatar.reencode.rejected", "requests", "闸门等待超时拒绝次数");
        Meter.CreateObservableGauge("avatar.reencode.queue_depth", () => Volatile.Read(ref _queued), "requests", "等待闸门的请求数");
        Meter.CreateObservableGauge("avatar.reencode.in_flight", () => Volatile.Read(ref _inFlight), "requests", "正在重编码的请求数");
    }

    public void BeginWait() => Interlocked.Increment(ref _queued);
    public void EndWait(double waitMilliseconds)
    {
        Interlocked.Decrement(ref _queued);
        _waitMs.Record(waitMilliseconds);
    }

    public void BeginWork() => Interlocked.Increment(ref _inFlight);
    public void EndWork(double workMilliseconds)
    {
        Interlocked.Decrement(ref _inFlight);
        _workMs.Record(workMilliseconds);
    }

    public void RecordRejected() => _rejected.Add(1);
}
