using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Services;
using Npgsql;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisPostgresCollection))]
public sealed class FormalAttachmentsTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [SkippableFact]
    public async Task Presign_Upload_Confirm_InsertsConfirmedRow()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await EnsureAttachmentsSchemaAsync(postgres.ConnectionString);

        var attachmentRoot = Path.Combine(Path.GetTempPath(), "chatapp-waf-att", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachmentRoot);

        await using var factory = new ChatAppWebApplicationFactory(
            postgres.ConnectionString,
            redis.ConnectionString,
            extraConfig: new Dictionary<string, string?>
            {
                ["AttachmentStorage:Provider"] = "Local",
                ["AttachmentStorage:LocalRootPath"] = attachmentRoot,
                ["AttachmentStorage:PublicBaseUrl"] = "/static/attachments",
                ["AttachmentStorage:MaxBytes"] = "26214400",
                ["AttachmentStorage:TicketMinutes"] = "15",
                ["AttachmentStorage:AllowedContentTypes:0"] = "image/png",
                ["AttachmentStorage:AllowedContentTypes:1"] = "application/octet-stream",
                ["MessageEvidence:RealtimeConnectionString"] = postgres.ConnectionString,
                ["MessageEvidence:Schema"] = "realtime",
            });

        using var client = factory.CreateClientWithDevice($"dev-att-{Guid.NewGuid():N}"[..24]);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var db = postgres.CreateContext())
        {
            await WafTestHelpers.SeedUserAsync(db, $"att-{suffix}", $"att-{suffix}@ex.com", "Passw0rd!");
        }

        var login = await WafTestHelpers.LoginAsync(client, $"att-{suffix}", "Passw0rd!");
        client.UseBearer(login.AccessToken!);

        var payload = Encoding.UTF8.GetBytes("formal-attachment-bytes");
        var presign = await client.PostAsJsonAsync("/api/attachments/presign", new
        {
            contentType = "application/octet-stream",
            contentLength = payload.Length,
            originalName = "note.bin",
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, presign.StatusCode);
        var ticket = await presign.Content.ReadFromJsonAsync<PresignDto>(WafTestHelpers.Json);
        Assert.False(string.IsNullOrWhiteSpace(ticket?.Ticket));
        Assert.False(string.IsNullOrWhiteSpace(ticket?.AttachmentId));
        Assert.False(string.IsNullOrWhiteSpace(ticket?.ObjectKey));

        using (var content = new ByteArrayContent(payload))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            var upload = await client.PutAsync(
                $"/api/attachments/upload?ticket={Uri.EscapeDataString(ticket!.Ticket)}", content);
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        }

        var confirm = await client.PostAsJsonAsync("/api/attachments/confirm", new
        {
            objectKey = ticket.ObjectKey,
            ticket = ticket.Ticket,
            attachmentId = ticket.AttachmentId,
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var body = await confirm.Content.ReadFromJsonAsync<ConfirmDto>(WafTestHelpers.Json);
        Assert.Equal(ticket.AttachmentId, body?.AttachmentId);
        Assert.False(string.IsNullOrWhiteSpace(body?.DownloadPath));
        Assert.Contains(ticket.AttachmentId, body!.DownloadPath, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(body.PublicUrl));

        await using var conn = new NpgsqlConnection(postgres.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT status, object_key, public_url, content_type, size_bytes
            FROM realtime.attachments
            WHERE attachment_id = @id
            """, conn);
        cmd.Parameters.AddWithValue("id", ticket.AttachmentId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal((short)AttachmentStatus.Confirmed, reader.GetInt16(0));
        Assert.Equal(ticket.ObjectKey, reader.GetString(1));
        // public_url 不再写入永久静态路径
        Assert.Equal("application/octet-stream", reader.GetString(3));
        Assert.Equal(payload.Length, reader.GetInt64(4));
    }

    [Fact]
    public async Task WriteChatExport_PrefersFormal_ThenLegacyParserDeduped()
    {
        var reader = new SingleMessageReader(
            new ChatExportMessage(
                "m1", "c1", 9, 10,
                """{"attachments":[{"url":"https://cdn.example/formal.png","name":"dup.png"}],"text":"also https://cdn.example/legacy.pdf"}""",
                100, null, null));

        var meta = new FakeAttachmentMetadataStore(
        [
            new AttachmentRecord(
                AttachmentId: "a1",
                UploaderUserId: 9,
                ObjectKey: "9/a1.png",
                PublicUrl: "https://cdn.example/formal.png",
                ContentType: "image/png",
                SizeBytes: 12,
                OriginalName: "formal.png",
                Status: AttachmentStatus.Confirmed,
                MessageId: "m1",
                ConversationId: null,
                ClientAttachmentId: null,
                CreatedAtMs: 90,
                ConfirmedAtMs: 95,
                BoundAtMs: null),
        ]);

        var opts = new DataExportStorageOptions
        {
            IncludeChatContent = true,
            ChatExportMaxMessages = 100,
            ChatExportMaxAttachmentUrls = 100,
        };

        await using var ms = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            await DataExportWorker.WriteChatExportAsync(
                writer, reader, meta, 9, opts, CancellationToken.None);
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(ms.ToArray());
        var attachments = doc.RootElement.GetProperty("attachments").EnumerateArray().ToList();
        Assert.Contains(attachments, a =>
            GetString(a, "Source", "source") == "formal"
            && GetString(a, "Url", "url") == "https://cdn.example/formal.png");
        Assert.Contains(attachments, a =>
            GetString(a, "Source", "source") == "url_scan"
            && (GetString(a, "Url", "url")?.Contains("legacy.pdf", StringComparison.Ordinal) ?? false));
        // formal URL should not be duplicated from JSON/parser
        Assert.Equal(1, attachments.Count(a =>
            GetString(a, "Url", "url") == "https://cdn.example/formal.png"));
        Assert.Equal(1, doc.RootElement.GetProperty("chatExport").GetProperty("formalAttachmentCount").GetInt32());
    }

    [Fact]
    public void Parser_StillWorks_ForLegacy()
    {
        var items = ChatExportAttachmentParser.Extract(
            "m9", 1,
            """{"attachments":[{"url":"https://cdn.example/legacy.png","name":"l.png","mime":"image/png"}]}""");
        Assert.Single(items);
        Assert.Equal("json", items[0].Source);
        Assert.Equal("https://cdn.example/legacy.png", items[0].Url);
    }

    private static async Task EnsureAttachmentsSchemaAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            CREATE SCHEMA IF NOT EXISTS realtime;
            CREATE TABLE IF NOT EXISTS realtime.attachments (
                attachment_id         varchar(64)   PRIMARY KEY,
                uploader_user_id      bigint        NOT NULL,
                object_key            varchar(512)  NOT NULL,
                public_url            varchar(1024) NULL,
                content_type          varchar(128)  NOT NULL,
                size_bytes            bigint        NOT NULL,
                original_name         varchar(256)  NULL,
                status                smallint      NOT NULL,
                message_id            varchar(64)   NULL,
                conversation_id       varchar(64)   NULL,
                client_attachment_id  varchar(128)  NULL,
                created_at_ms         bigint        NOT NULL,
                confirmed_at_ms       bigint        NULL,
                bound_at_ms           bigint        NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_attachments_object_key
                ON realtime.attachments (object_key);
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string? GetString(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p))
                return p.GetString();
        }

        return null;
    }

    private sealed record PresignDto(
        string AttachmentId, string UploadUrl, string DownloadPath, string PublicUrl, string ObjectKey, string Ticket, DateTimeOffset ExpiresAt);

    private sealed record ConfirmDto(string AttachmentId, string DownloadPath, string PublicUrl, string ObjectKey);

    private sealed class FakeAttachmentMetadataStore(IReadOnlyList<AttachmentRecord> rows) : IAttachmentMetadataStore
    {
        public bool IsAvailable => true;
        public string UnavailableReason => string.Empty;

        public Task InsertTicketedAsync(
            string attachmentId, long uploaderUserId, string objectKey, string? publicUrl,
            string contentType, long sizeBytes, string? originalName, string? clientAttachmentId = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ConfirmAsync(
            string attachmentId, long uploaderUserId, string objectKey, string? publicUrl,
            string contentType, long sizeBytes, string? originalName = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task MarkUploadedScanningAsync(
            string attachmentId, long uploaderUserId, long sizeBytes, string? sha256Hex = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task MarkRejectedAsync(
            string attachmentId, long uploaderUserId, string? reason = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<Core.Models.Attachment.AttachmentDownloadAccess> ResolveDownloadAccessAsync(
            string attachmentId, long userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new Core.Models.Attachment.AttachmentDownloadAccess(
                attachmentId, string.Empty, "application/octet-stream", null,
                Core.Models.Attachment.AttachmentDownloadDecision.NotFound));

        public Task<IReadOnlyList<AttachmentRecord>> ListForExportAsync(
            long userId, int maxRows = 50_000, CancellationToken cancellationToken = default)
            => Task.FromResult(rows);

        public Task<IReadOnlyList<string>> ListObjectKeysForUserAsync(
            long uploaderUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(rows.Select(r => r.ObjectKey).ToList());

        public Task<IReadOnlySet<string>> ListActiveObjectKeysAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<string>>(
                new HashSet<string>(rows.Select(r => r.ObjectKey), StringComparer.Ordinal));

        public Task MarkAbandonedAsync(IReadOnlyList<string> attachmentIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task MarkAbandonedByUploaderAsync(long uploaderUserId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string?> TryAbandonUnboundByUploaderAsync(
            string attachmentId,
            long uploaderUserId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class SingleMessageReader(ChatExportMessage msg) : IRealtimeChatExportReader
    {
        public bool IsAvailable => true;
        public string UnavailableReason => string.Empty;

        public Task<ChatExportPage> ReadPageAsync(
            long userId, long? beforeReceivedAtMs, string? beforeMessageId, int take,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatExportPage([msg], false, null, null));

        public async IAsyncEnumerable<ChatExportMessage> ReadMessagesAsync(
            long userId, int maxMessages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield return msg;
        }
    }
}
