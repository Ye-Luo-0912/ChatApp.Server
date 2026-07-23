using System.Runtime.CompilerServices;
using ChatApp.Realtime.Abstractions.Messaging.History;
using ChatApp.Realtime.Integration;
using Core.Interfaces;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.Services;

/// <summary>
/// 账号导出用聊天读取：优先直连 Realtime Postgres（单连接连续读），
/// 否则经 NATS 历史查询分页 yield；皆不可用时由调用方标注 unavailable。
/// </summary>
public sealed class RealtimeChatExportReader : IRealtimeChatExportReader
{
    private readonly MessageEvidenceOptions _evidence;
    private readonly DataExportStorageOptions _export;
    private readonly IRealtimeMessageBus? _bus;
    private readonly ILogger<RealtimeChatExportReader> _logger;

    public RealtimeChatExportReader(
        IOptions<MessageEvidenceOptions> evidence,
        IOptions<DataExportStorageOptions> export,
        ILogger<RealtimeChatExportReader> logger,
        IRealtimeMessageBus? bus = null)
    {
        _evidence = evidence.Value;
        _export = export.Value;
        _bus = bus;
        _logger = logger;
    }

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(ResolveConnectionString()) || _bus is not null;

    public string UnavailableReason =>
        IsAvailable
            ? string.Empty
            : "未配置 MessageEvidence:RealtimeConnectionString / DataExport:RealtimeConnectionString，且无 Realtime NATS 总线";

    public async Task<ChatExportPage> ReadPageAsync(
        long userId,
        long? beforeReceivedAtMs,
        string? beforeMessageId,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        take = Math.Clamp(take, 1, Math.Max(1, _export.ChatExportPageSize));

        var cs = ResolveConnectionString();
        if (!string.IsNullOrWhiteSpace(cs))
            return await ReadFromPostgresAsync(cs!, userId, beforeReceivedAtMs, beforeMessageId, take, cancellationToken)
                .ConfigureAwait(false);

        if (_bus is not null)
            return await ReadFromBusAsync(userId, beforeReceivedAtMs, beforeMessageId, take, cancellationToken)
                .ConfigureAwait(false);

        return new ChatExportPage([], false, null, null);
    }

    public async IAsyncEnumerable<ChatExportMessage> ReadMessagesAsync(
        long userId,
        int maxMessages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        maxMessages = Math.Max(1, maxMessages);

        var cs = ResolveConnectionString();
        if (!string.IsNullOrWhiteSpace(cs))
        {
            await foreach (var msg in StreamFromPostgresAsync(cs!, userId, maxMessages, cancellationToken)
                               .ConfigureAwait(false))
                yield return msg;
            yield break;
        }

        if (_bus is null)
            yield break;

        // NATS：按页拉取并 yield，避免一次缓冲全部。
        var pageSize = Math.Clamp(_export.ChatExportPageSize, 1, 100);
        long? beforeAt = null;
        string? beforeId = null;
        var yielded = 0;
        while (yielded < maxMessages)
        {
            var take = Math.Min(pageSize, maxMessages - yielded);
            var page = await ReadFromBusAsync(userId, beforeAt, beforeId, take, cancellationToken)
                .ConfigureAwait(false);
            if (page.Items.Count == 0)
                yield break;

            foreach (var msg in page.Items)
            {
                yield return msg;
                yielded++;
                if (yielded >= maxMessages)
                    yield break;
            }

            if (!page.HasMore
                || page.NextBeforeReceivedAtMs is null
                || string.IsNullOrWhiteSpace(page.NextBeforeMessageId))
                yield break;

            beforeAt = page.NextBeforeReceivedAtMs;
            beforeId = page.NextBeforeMessageId;
        }
    }

    private string? ResolveConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(_export.RealtimeConnectionString))
            return _export.RealtimeConnectionString;
        if (!string.IsNullOrWhiteSpace(_evidence.RealtimeConnectionString))
            return _evidence.RealtimeConnectionString;
        return null;
    }

    private async IAsyncEnumerable<ChatExportMessage> StreamFromPostgresAsync(
        string connectionString,
        long userId,
        int maxMessages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var schema = string.IsNullOrWhiteSpace(_evidence.Schema) ? "realtime" : _evidence.Schema.Trim();
        var table = $"\"{schema}\".\"messages\"";

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        // 单连接连续读：UNION ALL 两侧各取上限，外层再 LIMIT，避免分页 N 次往返。
        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT
                 history.message_id,
                 history.client_message_id,
                 history.sender_user_id,
                 history.receiver_user_id,
                 history.content,
                 history.received_at_ms,
                 history.delivered_at_ms,
                 history.read_at_ms
             FROM (
                 (
                     SELECT
                         message_id, client_message_id, sender_user_id, receiver_user_id,
                         content, received_at_ms, delivered_at_ms, read_at_ms
                     FROM {table}
                     WHERE receiver_user_id = @user_id
                     ORDER BY received_at_ms DESC, message_id DESC
                     LIMIT @take
                 )
                 UNION ALL
                 (
                     SELECT
                         message_id, client_message_id, sender_user_id, receiver_user_id,
                         content, received_at_ms, delivered_at_ms, read_at_ms
                     FROM {table}
                     WHERE sender_user_id = @user_id
                       AND receiver_user_id <> @user_id
                     ORDER BY received_at_ms DESC, message_id DESC
                     LIMIT @take
                 )
             ) AS history
             ORDER BY history.received_at_ms DESC, history.message_id DESC
             LIMIT @take;
             """,
            conn);

        cmd.Parameters.AddWithValue("user_id", userId);
        cmd.Parameters.AddWithValue("take", maxMessages);

        await using var reader = await cmd.ExecuteReaderAsync(
                System.Data.CommandBehavior.SequentialAccess, cancellationToken)
            .ConfigureAwait(false);

        var count = 0;
        while (count < maxMessages
               && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new ChatExportMessage(
                MessageId: reader.GetString(0),
                ClientMessageId: reader.GetString(1),
                SenderUserId: reader.GetInt64(2),
                ReceiverUserId: reader.GetInt64(3),
                Content: reader.GetString(4),
                ReceivedAtMs: reader.GetInt64(5),
                DeliveredAtMs: reader.IsDBNull(6) ? null : reader.GetInt64(6),
                ReadAtMs: reader.IsDBNull(7) ? null : reader.GetInt64(7));
            count++;
        }
    }

    private async Task<ChatExportPage> ReadFromPostgresAsync(
        string connectionString,
        long userId,
        long? beforeReceivedAtMs,
        string? beforeMessageId,
        int take,
        CancellationToken cancellationToken)
    {
        var schema = string.IsNullOrWhiteSpace(_evidence.Schema) ? "realtime" : _evidence.Schema.Trim();
        var table = $"\"{schema}\".\"messages\"";
        var fetch = take + 1;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT
                 history.message_id,
                 history.client_message_id,
                 history.sender_user_id,
                 history.receiver_user_id,
                 history.content,
                 history.received_at_ms,
                 history.delivered_at_ms,
                 history.read_at_ms
             FROM (
                 (
                     SELECT
                         message_id, client_message_id, sender_user_id, receiver_user_id,
                         content, received_at_ms, delivered_at_ms, read_at_ms
                     FROM {table}
                     WHERE receiver_user_id = @user_id
                       AND (
                           @before_received_at_ms IS NULL
                           OR received_at_ms < @before_received_at_ms
                           OR (
                               received_at_ms = @before_received_at_ms
                               AND message_id < @before_message_id
                           )
                       )
                     ORDER BY received_at_ms DESC, message_id DESC
                     LIMIT @take
                 )
                 UNION ALL
                 (
                     SELECT
                         message_id, client_message_id, sender_user_id, receiver_user_id,
                         content, received_at_ms, delivered_at_ms, read_at_ms
                     FROM {table}
                     WHERE sender_user_id = @user_id
                       AND receiver_user_id <> @user_id
                       AND (
                           @before_received_at_ms IS NULL
                           OR received_at_ms < @before_received_at_ms
                           OR (
                               received_at_ms = @before_received_at_ms
                               AND message_id < @before_message_id
                           )
                       )
                     ORDER BY received_at_ms DESC, message_id DESC
                     LIMIT @take
                 )
             ) AS history
             ORDER BY history.received_at_ms DESC, history.message_id DESC
             LIMIT @take;
             """,
            conn);

        cmd.Parameters.AddWithValue("user_id", userId);
        cmd.Parameters.AddWithValue("take", fetch);
        cmd.Parameters.Add("before_received_at_ms", NpgsqlDbType.Bigint).Value =
            beforeReceivedAtMs.HasValue ? beforeReceivedAtMs.Value : DBNull.Value;
        cmd.Parameters.Add("before_message_id", NpgsqlDbType.Varchar).Value =
            beforeMessageId is not null ? beforeMessageId : DBNull.Value;

        var items = new List<ChatExportMessage>(fetch);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new ChatExportMessage(
                MessageId: reader.GetString(0),
                ClientMessageId: reader.GetString(1),
                SenderUserId: reader.GetInt64(2),
                ReceiverUserId: reader.GetInt64(3),
                Content: reader.GetString(4),
                ReceivedAtMs: reader.GetInt64(5),
                DeliveredAtMs: reader.IsDBNull(6) ? null : reader.GetInt64(6),
                ReadAtMs: reader.IsDBNull(7) ? null : reader.GetInt64(7)));
        }

        var hasMore = items.Count > take;
        if (hasMore)
            items.RemoveAt(items.Count - 1);

        long? nextAt = null;
        string? nextId = null;
        if (hasMore && items.Count > 0)
        {
            var last = items[^1];
            nextAt = last.ReceivedAtMs;
            nextId = last.MessageId;
        }

        return new ChatExportPage(items, hasMore, nextAt, nextId);
    }

    private async Task<ChatExportPage> ReadFromBusAsync(
        long userId,
        long? beforeReceivedAtMs,
        string? beforeMessageId,
        int take,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(take, 1, 100);
        var page = await _bus!.QueryMessageHistoryAsync(
                new MessageHistoryQuery
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    UserId = userId,
                    BeforeReceivedAtMs = beforeReceivedAtMs,
                    BeforeMessageId = beforeMessageId,
                    Limit = pageSize,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!page.Succeeded)
        {
            _logger.LogWarning(
                "Realtime 历史查询失败 UserId={UserId} Code={Code} Msg={Message}",
                userId, page.ErrorCode, page.ErrorMessage);
            throw new InvalidOperationException(
                $"Realtime 历史查询失败: {page.ErrorCode ?? "unknown"}");
        }

        var items = page.Items.Select(m => new ChatExportMessage(
            m.MessageId,
            m.ClientMessageId,
            m.SenderUserId,
            m.ReceiverUserId,
            m.Content,
            m.ReceivedAtMs,
            m.DeliveredAtMs,
            m.ReadAtMs)).ToList();

        return new ChatExportPage(
            items,
            page.HasMore,
            page.NextCursor?.ReceivedAtMs,
            page.NextCursor?.MessageId);
    }
}

/// <summary>测试/未接线时的空实现。</summary>
public sealed class UnavailableRealtimeChatExportReader : IRealtimeChatExportReader
{
    public bool IsAvailable => false;
    public string UnavailableReason => "Realtime 聊天导出源未配置";

    public Task<ChatExportPage> ReadPageAsync(
        long userId,
        long? beforeReceivedAtMs,
        string? beforeMessageId,
        int take,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatExportPage([], false, null, null));

    public async IAsyncEnumerable<ChatExportMessage> ReadMessagesAsync(
        long userId,
        int maxMessages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
