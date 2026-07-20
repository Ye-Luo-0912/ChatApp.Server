namespace Core.Interfaces;

/// <summary>
/// 从消息服务拉取服务端可信证据；不得使用举报人提交的原文。
/// </summary>
public interface IMessageEvidenceProvider
{
    Task<MessageEvidenceSnapshot?> TryGetAsync(string messageId, CancellationToken cancellationToken = default);
}

/// <summary>消息证据快照（原文来自服务端，含内容哈希便于完整性核对）。</summary>
public sealed record MessageEvidenceSnapshot(
    string MessageId,
    long SenderUserId,
    DateTimeOffset SentAtUtc,
    string ContentHashSha256,
    string BodyText);
