namespace Core.Interfaces;

/// <summary>
/// 从消息服务拉取服务端可信证据；不得使用举报人提交的原文。
/// </summary>
public interface IMessageEvidenceProvider
{
    /// <param name="requestingUserId">
    /// 可选：经 NATS 查询时须为消息参与方；直连 DB 时可为空（由调用方再校验参与关系）。
    /// </param>
    Task<MessageEvidenceSnapshot?> TryGetAsync(
        string messageId,
        long? requestingUserId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>消息证据快照（原文来自服务端，含内容哈希便于完整性核对）。</summary>
public sealed record MessageEvidenceSnapshot(
    string MessageId,
    long SenderUserId,
    long ReceiverUserId,
    DateTimeOffset SentAtUtc,
    string ContentHashSha256,
    string BodyText,
    /// <summary>内容版本，从 1 起；每次成功编辑 +1。</summary>
    int EditVersion = 1,
    /// <summary>最近一次成功编辑时间（Unix ms）；未编辑为 null。</summary>
    long? EditedAtMs = null,
    /// <summary>撤回时间（Unix ms）；非空表示已撤回，BodyText 应为空 stub。</summary>
    long? RecalledAtMs = null)
{
    /// <summary>是否已撤回（软撤回 stub）。</summary>
    public bool IsRecalled => RecalledAtMs is > 0;
}
