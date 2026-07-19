using System.Threading.Channels;
using Core.Models.Email;

namespace Infrastructure.Services.Email;

public sealed record EmailWorkItem(
    string To,
    string Subject,
    string Body,
    bool IsHtml,
    TaskCompletionSource<EmailResult> Completion);

/// <summary>
/// 有界邮件发送队列，API 只负责入队。
/// </summary>
public sealed class EmailQueue
{
    private readonly Channel<EmailWorkItem> _channel = Channel.CreateBounded<EmailWorkItem>(
        new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });

    public ValueTask EnqueueAsync(EmailWorkItem item, CancellationToken cancellationToken)
        => _channel.Writer.WriteAsync(item, cancellationToken);

    public IAsyncEnumerable<EmailWorkItem> ReadAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
