namespace Core.Interfaces;

/// <summary>按用户读取 Realtime 聊天数据，供账号数据导出使用。</summary>
public interface IRealtimeChatExportReader
{
    /// <summary>
    /// 是否已配置可读来源（Realtime Postgres 或 NATS 历史查询）。
    /// 未配置时导出仍应成功，但 messages/receipts/attachments 为空并注明原因。
    /// </summary>
    bool IsAvailable { get; }

    string UnavailableReason { get; }

    /// <summary>
    /// 拉取一页消息（发送方或接收方），按 received_at_ms DESC, message_id DESC。
    /// </summary>
    Task<ChatExportPage> ReadPageAsync(
        long userId,
        long? beforeReceivedAtMs,
        string? beforeMessageId,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 流式读取消息：Postgres 路径尽量单连接连续读；NATS 回退仍按页拉取并 yield。
    /// <paramref name="maxMessages"/> 为硬上限（含）。
    /// </summary>
    IAsyncEnumerable<ChatExportMessage> ReadMessagesAsync(
        long userId,
        int maxMessages,
        CancellationToken cancellationToken = default);
}

public sealed record ChatExportPage(
    IReadOnlyList<ChatExportMessage> Items,
    bool HasMore,
    long? NextBeforeReceivedAtMs,
    string? NextBeforeMessageId);

public sealed record ChatExportMessage(
    string MessageId,
    string ClientMessageId,
    long SenderUserId,
    long ReceiverUserId,
    string Content,
    long ReceivedAtMs,
    long? DeliveredAtMs,
    long? ReadAtMs);
