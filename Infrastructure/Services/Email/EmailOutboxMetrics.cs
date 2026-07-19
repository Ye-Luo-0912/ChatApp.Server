using System.Diagnostics.Metrics;

namespace Infrastructure.Services.Email;

public sealed class EmailOutboxMetrics
{
    private static readonly Meter Meter = new("Infrastructure.Email.Outbox");

    private readonly Counter<long> _enqueued;
    private readonly Counter<long> _sent;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _dead;

    public EmailOutboxMetrics()
    {
        _enqueued = Meter.CreateCounter<long>("email.outbox.enqueued", "messages", "邮件入队次数");
        _sent = Meter.CreateCounter<long>("email.outbox.sent", "messages", "邮件发送成功次数");
        _failed = Meter.CreateCounter<long>("email.outbox.failed", "messages", "邮件发送失败次数");
        _dead = Meter.CreateCounter<long>("email.outbox.dead", "messages", "邮件进入死信次数");
    }

    public void RecordEnqueued() => _enqueued.Add(1);
    public void RecordSent() => _sent.Add(1);
    public void RecordFailed() => _failed.Add(1);
    public void RecordDead() => _dead.Add(1);
}
