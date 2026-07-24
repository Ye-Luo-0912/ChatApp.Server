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
    long? ReadAtMs,
    /// <summary>内容版本，从 1 起；每次成功编辑 +1。</summary>
    int EditVersion = 1,
    /// <summary>最近一次成功编辑时间（Unix ms）；未编辑为 null。</summary>
    long? EditedAtMs = null,
    /// <summary>撤回时间（Unix ms）；非空表示已撤回，Content 应为空 stub。</summary>
    long? RecalledAtMs = null)
{
    /// <summary>是否已撤回（软撤回 stub）。</summary>
    public bool IsRecalled => RecalledAtMs is > 0;

    /// <summary>是否已编辑过。</summary>
    public bool IsEdited => EditVersion > 1 || EditedAtMs is > 0;
}
