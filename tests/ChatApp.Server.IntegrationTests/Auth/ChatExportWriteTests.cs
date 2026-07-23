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
