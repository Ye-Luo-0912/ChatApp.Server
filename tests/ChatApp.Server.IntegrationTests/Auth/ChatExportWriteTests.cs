using System.Text;
using System.Text.Json;
using Core.Interfaces;
using Core.Settings;
using Infrastructure.Services;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

public sealed class ChatExportWriteTests
{
    [Fact]
    public async Task WriteChatExport_Streams_ReceiptsAndAttachments_WithoutHoldingAllMessages()
    {
        var reader = new PagingFakeReader();
        var opts = new DataExportStorageOptions
        {
            IncludeChatContent = true,
            ChatExportPageSize = 2,
            ChatExportMaxMessages = 100,
        };

        await using var ms = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            await DataExportWorker.WriteChatExportAsync(writer, reader, 9, opts, CancellationToken.None);
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(ms.ToArray());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(3, root.GetProperty("messages").GetArrayLength());
        Assert.Equal(2, root.GetProperty("receipts").GetArrayLength());
        Assert.Contains(
            root.GetProperty("attachments").EnumerateArray(),
            a =>
            {
                var url = a.TryGetProperty("url", out var u) ? u.GetString()
                    : a.TryGetProperty("Url", out var U) ? U.GetString()
                    : null;
                return url is not null && url.Contains("photo.jpg", StringComparison.Ordinal);
            });
        Assert.Equal("ok", root.GetProperty("chatExport").GetProperty("status").GetString());
        Assert.Contains("m-a", json, StringComparison.Ordinal);
        Assert.Contains("m-c", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteChatExport_RespectsMaxMessages_Truncates()
    {
        var reader = new PagingFakeReader();
        var opts = new DataExportStorageOptions
        {
            IncludeChatContent = true,
            ChatExportMaxMessages = 2,
        };

        await using var ms = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            await DataExportWorker.WriteChatExportAsync(writer, reader, 9, opts, CancellationToken.None);
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(ms.ToArray());
        Assert.Equal(2, doc.RootElement.GetProperty("messages").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("chatExport").GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task WriteChatExport_IncludesEditedMessageVersion()
    {
        var reader = new FixedMessagesReader(
        [
            new ChatExportMessage(
                "m-edited", "c-edited", 9, 10,
                "after-edit body",
                500, null, null,
                EditVersion: 3,
                EditedAtMs: 1_700_000_000_500),
        ]);
        var opts = new DataExportStorageOptions
        {
            IncludeChatContent = true,
            ChatExportMaxMessages = 100,
        };

        await using var ms = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            await DataExportWorker.WriteChatExportAsync(writer, reader, 9, opts, CancellationToken.None);
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(ms.ToArray());
        var msg = doc.RootElement.GetProperty("messages")[0];
        Assert.Equal("m-edited", GetProp(msg, "MessageId", "messageId"));
        Assert.Equal("after-edit body", GetProp(msg, "Content", "content"));
        Assert.Equal(3, GetInt(msg, "EditVersion", "editVersion"));
        Assert.Equal(1_700_000_000_500L, GetLong(msg, "EditedAtMs", "editedAtMs"));
        Assert.False(GetBool(msg, "IsRecalled", "isRecalled"));
        Assert.True(IsNullOrMissing(msg, "RecalledAtMs", "recalledAtMs"));
    }

    [Fact]
    public async Task WriteChatExport_RecalledMessage_AppearsAsStubWithoutOriginalBody()
    {
        // Reader may still carry leaked body; writer must redact when RecalledAtMs is set.
        var reader = new FixedMessagesReader(
        [
            new ChatExportMessage(
                "m-recalled", "c-recalled", 9, 10,
                "SECRET_ORIGINAL_BODY https://cdn.example/leak.jpg",
                400, 401, null,
                EditVersion: 2,
                EditedAtMs: 1_700_000_000_300,
                RecalledAtMs: 1_700_000_000_400),
        ]);
        var opts = new DataExportStorageOptions
        {
            IncludeChatContent = true,
            ChatExportMaxMessages = 100,
        };

        await using var ms = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            await DataExportWorker.WriteChatExportAsync(writer, reader, 9, opts, CancellationToken.None);
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(ms.ToArray());
        Assert.DoesNotContain("SECRET_ORIGINAL_BODY", json, StringComparison.Ordinal);
        Assert.DoesNotContain("leak.jpg", json, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(json);
        var msg = doc.RootElement.GetProperty("messages")[0];
        Assert.Equal("m-recalled", GetProp(msg, "MessageId", "messageId"));
        Assert.Equal(string.Empty, GetProp(msg, "Content", "content") ?? "missing");
        Assert.True(GetBool(msg, "IsRecalled", "isRecalled"));
        Assert.Equal(1_700_000_000_400L, GetLong(msg, "RecalledAtMs", "recalledAtMs"));
        Assert.Equal(2, GetInt(msg, "EditVersion", "editVersion"));
        Assert.Equal(0, doc.RootElement.GetProperty("attachments").GetArrayLength());
    }

    private static string? GetProp(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p))
                return p.ValueKind == JsonValueKind.Null ? null : p.GetString();
        }

        return null;
    }

    private static int GetInt(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p) && p.TryGetInt32(out var v))
                return v;
        }

        throw new InvalidOperationException($"Missing int property among: {string.Join(',', names)}");
    }

    private static long GetLong(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p) && p.TryGetInt64(out var v))
                return v;
        }

        throw new InvalidOperationException($"Missing long property among: {string.Join(',', names)}");
    }

    private static bool GetBool(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p) && (p.ValueKind is JsonValueKind.True or JsonValueKind.False))
                return p.GetBoolean();
        }

        throw new InvalidOperationException($"Missing bool property among: {string.Join(',', names)}");
    }

    private static bool IsNullOrMissing(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p))
                return p.ValueKind is JsonValueKind.Null;
        }

        return true;
    }

    private sealed class FixedMessagesReader(IReadOnlyList<ChatExportMessage> messages) : IRealtimeChatExportReader
    {
        public bool IsAvailable => true;
        public string UnavailableReason => string.Empty;

        public Task<ChatExportPage> ReadPageAsync(
            long userId,
            long? beforeReceivedAtMs,
            string? beforeMessageId,
            int take,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatExportPage(messages.Take(take).ToList(), false, null, null));

        public async IAsyncEnumerable<ChatExportMessage> ReadMessagesAsync(
            long userId,
            int maxMessages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            foreach (var msg in messages.Take(maxMessages))
                yield return msg;
        }
    }

    private sealed class PagingFakeReader : IRealtimeChatExportReader
    {
        private readonly List<ChatExportMessage> _all =
        [
            new("m-a", "c-a", 9, 10, "hi https://cdn.example/photo.jpg", 300, 301, null),
            new("m-b", "c-b", 10, 9, "yo", 200, null, null),
            new("m-c", "c-c", 9, 11, "bye", 100, 110, 120),
        ];

        public bool IsAvailable => true;
        public string UnavailableReason => string.Empty;

        public Task<ChatExportPage> ReadPageAsync(
            long userId,
            long? beforeReceivedAtMs,
            string? beforeMessageId,
            int take,
            CancellationToken cancellationToken = default)
        {
            IEnumerable<ChatExportMessage> q = _all;
            if (beforeReceivedAtMs is { } at)
            {
                q = q.Where(m =>
                    m.ReceivedAtMs < at
                    || (m.ReceivedAtMs == at
                        && string.CompareOrdinal(m.MessageId, beforeMessageId) < 0));
            }

            var slice = q.Take(take + 1).ToList();
            var hasMore = slice.Count > take;
            if (hasMore) slice.RemoveAt(slice.Count - 1);
            long? nextAt = hasMore && slice.Count > 0 ? slice[^1].ReceivedAtMs : null;
            string? nextId = hasMore && slice.Count > 0 ? slice[^1].MessageId : null;
            return Task.FromResult(new ChatExportPage(slice, hasMore, nextAt, nextId));
        }

        public async IAsyncEnumerable<ChatExportMessage> ReadMessagesAsync(
            long userId,
            int maxMessages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            foreach (var msg in _all.Take(maxMessages))
                yield return msg;
        }
    }
}
