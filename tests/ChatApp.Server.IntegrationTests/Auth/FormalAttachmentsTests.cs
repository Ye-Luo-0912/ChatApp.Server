using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Services;
using Infrastructure.Serialization;
using Microsoft.Extensions.DependencyInjection;
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

        await RealtimeAttachmentTestSchema.EnsureAsync(postgres.ConnectionString);

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
        Assert.Equal(HttpStatusCode.Accepted, confirm.StatusCode);
        var body = await confirm.Content.ReadFromJsonAsync<ConfirmDto>(WafTestHelpers.Json);
        Assert.Equal(ticket.AttachmentId, body?.AttachmentId);
        Assert.False(string.IsNullOrWhiteSpace(body?.DownloadPath));
        Assert.Contains(ticket.AttachmentId, body!.DownloadPath, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(body.PublicUrl));
        Assert.Equal("Scanning", body.Status);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var scans = scope.ServiceProvider.GetRequiredService<IAttachmentScanService>();
            Assert.True(await scans.ProcessDueAsync() >= 1);
        }

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

    [SkippableFact]
    public async Task Presign_StorageQuotaExceeded_DoesNotReturnUploadUrl()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await RealtimeAttachmentTestSchema.EnsureAsync(postgres.ConnectionString);

        var attachmentRoot = Path.Combine(Path.GetTempPath(), "chatapp-quota-att", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachmentRoot);

        await using var factory = new ChatAppWebApplicationFactory(
            postgres.ConnectionString,
            redis.ConnectionString,
            extraConfig: new Dictionary<string, string?>
            {
                ["AttachmentStorage:Provider"] = "Local",
                ["AttachmentStorage:LocalRootPath"] = attachmentRoot,
                ["AttachmentStorage:MaxBytes"] = "1024",
                ["AttachmentStorage:MaxUnconfirmedObjectsPerUser"] = "20",
                ["AttachmentStorage:MaxStorageBytesPerUser"] = "1024",
                ["AttachmentStorage:AllowedContentTypes:0"] = "application/octet-stream",
                ["MessageEvidence:RealtimeConnectionString"] = postgres.ConnectionString,
                ["MessageEvidence:Schema"] = "realtime",
            });

        using var client = factory.CreateClientWithDevice($"dev-quota-{Guid.NewGuid():N}"[..24]);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var db = postgres.CreateContext())
        {
            await WafTestHelpers.SeedUserAsync(db, $"quota-{suffix}", $"quota-{suffix}@ex.com", "Passw0rd!");
        }

        var login = await WafTestHelpers.LoginAsync(client, $"quota-{suffix}", "Passw0rd!");
        client.UseBearer(login.AccessToken!);

        var first = await client.PostAsJsonAsync("/api/attachments/presign", new
        {
            contentType = "application/octet-stream",
            contentLength = 1,
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var denied = await client.PostAsJsonAsync("/api/attachments/presign", new
        {
            contentType = "application/octet-stream",
            contentLength = 1,
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);

        using var body = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.Equal("AttachmentStorageQuotaExceeded", body.RootElement.GetProperty("code").GetString());
        Assert.False(body.RootElement.TryGetProperty("uploadUrl", out _));
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
        var writer = new SequentialJsonObjectWriter(ms);
        await writer.StartAsync();
        await DataExportWorker.WriteChatExportAsync(
            writer, reader, meta, 9, opts, CancellationToken.None);
        await writer.CompleteAsync();

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

    [SkippableFact]
    public async Task Presign_Sha256Dedup_Hit_ReturnsDeduplicated_SkipsUpload_Confirms()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await RealtimeAttachmentTestSchema.EnsureAsync(postgres.ConnectionString);

        var attachmentRoot = Path.Combine(Path.GetTempPath(), "chatapp-waf-dedup", Guid.NewGuid().ToString("N"));
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

        using var client = factory.CreateClientWithDevice($"dev-dedup-{Guid.NewGuid():N}"[..24]);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var db = postgres.CreateContext())
        {
            await WafTestHelpers.SeedUserAsync(db, $"dedup-{suffix}", $"dedup-{suffix}@ex.com", "Passw0rd!");
        }

        var login = await WafTestHelpers.LoginAsync(client, $"dedup-{suffix}", "Passw0rd!");
        client.UseBearer(login.AccessToken!);

        var payload = Encoding.UTF8.GetBytes("dedup-me-please-bytes");
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));

        // 第一步：普通上传建立已确认候选（content_hash 由扫描管线写回）。
        var first = await client.PostAsJsonAsync("/api/attachments/presign", new
        {
            contentType = "application/octet-stream",
            contentLength = payload.Length,
            originalName = "first.bin",
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstTicket = await first.Content.ReadFromJsonAsync<PresignDto>(WafTestHelpers.Json);
        Assert.False(firstTicket!.Deduplicated);

        using (var content = new ByteArrayContent(payload))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            var upload = await client.PutAsync(
                $"/api/attachments/upload?ticket={Uri.EscapeDataString(firstTicket.Ticket)}", content);
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        }

        var firstConfirm = await client.PostAsJsonAsync("/api/attachments/confirm", new
        {
            objectKey = firstTicket.ObjectKey,
            ticket = firstTicket.Ticket,
            attachmentId = firstTicket.AttachmentId,
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.Accepted, firstConfirm.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var scans = scope.ServiceProvider.GetRequiredService<IAttachmentScanService>();
            Assert.True(await scans.ProcessDueAsync() >= 1);
        }

        // 候选就绪：第二次 Presign 带 Sha256 必须命中 → Deduplicated、无上传 URL。
        var second = await client.PostAsJsonAsync("/api/attachments/presign", new
        {
            contentType = "application/octet-stream",
            contentLength = payload.Length,
            originalName = "second.bin",
            sha256,
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondTicket = await second.Content.ReadFromJsonAsync<PresignDto>(WafTestHelpers.Json);
        Assert.True(secondTicket!.Deduplicated);
        Assert.True(string.IsNullOrEmpty(secondTicket.UploadUrl));
        Assert.False(string.IsNullOrWhiteSpace(secondTicket.Ticket));
        Assert.False(string.IsNullOrWhiteSpace(secondTicket.ObjectKey));

        // 秒传命中后不 PUT：直接 Confirm，服务端从源对象复制。
        var secondConfirm = await client.PostAsJsonAsync("/api/attachments/confirm", new
        {
            objectKey = secondTicket.ObjectKey,
            ticket = secondTicket.Ticket,
            attachmentId = secondTicket.AttachmentId,
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.Accepted, secondConfirm.StatusCode);
        var secondBody = await secondConfirm.Content.ReadFromJsonAsync<ConfirmDto>(WafTestHelpers.Json);
        Assert.Equal(secondTicket.AttachmentId, secondBody?.AttachmentId);
        Assert.Equal("Scanning", secondBody!.Status);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var scans = scope.ServiceProvider.GetRequiredService<IAttachmentScanService>();
            Assert.True(await scans.ProcessDueAsync() >= 1);
        }

        // 两个附件最终 Confirmed，且 content_hash 一致（同一内容）。
        await using var conn = new NpgsqlConnection(postgres.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT status, content_hash, object_key
            FROM realtime.attachments
            WHERE attachment_id = ANY(@ids)
            ORDER BY attachment_id
            """, conn);
        cmd.Parameters.AddWithValue("ids", new[] { firstTicket.AttachmentId, secondTicket.AttachmentId });
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(short Status, string? Hash, string Key)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetInt16(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((short)AttachmentStatus.Confirmed, r.Status));
        Assert.All(rows, r => Assert.Equal(sha256, r.Hash));
        Assert.NotEqual(rows[0].Key, rows[1].Key);

        // 秒传目标对象已实际落盘（服务端复制，非客户端上传）。
        var secondFilePath = Path.Combine(attachmentRoot, secondTicket.ObjectKey.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(secondFilePath));
        Assert.Equal(payload, File.ReadAllBytes(secondFilePath));
    }

    [SkippableFact]
    public async Task Presign_Sha256Dedup_Miss_FallsBackToRegularTicket()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await RealtimeAttachmentTestSchema.EnsureAsync(postgres.ConnectionString);

        var attachmentRoot = Path.Combine(Path.GetTempPath(), "chatapp-waf-dedupmiss", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachmentRoot);

        await using var factory = new ChatAppWebApplicationFactory(
            postgres.ConnectionString,
            redis.ConnectionString,
            extraConfig: new Dictionary<string, string?>
            {
                ["AttachmentStorage:Provider"] = "Local",
                ["AttachmentStorage:LocalRootPath"] = attachmentRoot,
                ["AttachmentStorage:MaxBytes"] = "26214400",
                ["AttachmentStorage:TicketMinutes"] = "15",
                ["AttachmentStorage:AllowedContentTypes:0"] = "application/octet-stream",
                ["MessageEvidence:RealtimeConnectionString"] = postgres.ConnectionString,
                ["MessageEvidence:Schema"] = "realtime",
            });

        using var client = factory.CreateClientWithDevice($"dev-dedupm-{Guid.NewGuid():N}"[..24]);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var db = postgres.CreateContext())
        {
            await WafTestHelpers.SeedUserAsync(db, $"dedupm-{suffix}", $"dedupm-{suffix}@ex.com", "Passw0rd!");
        }

        var login = await WafTestHelpers.LoginAsync(client, $"dedupm-{suffix}", "Passw0rd!");
        client.UseBearer(login.AccessToken!);

        // 无匹配候选：Sha256 合法但库中无此内容 → 必须回退普通上传票。
        var miss = await client.PostAsJsonAsync("/api/attachments/presign", new
        {
            contentType = "application/octet-stream",
            contentLength = 4,
            sha256 = new string('b', 64),
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, miss.StatusCode);
        var ticket = await miss.Content.ReadFromJsonAsync<PresignDto>(WafTestHelpers.Json);
        Assert.False(ticket!.Deduplicated);
        Assert.False(string.IsNullOrWhiteSpace(ticket.UploadUrl));
        Assert.False(string.IsNullOrWhiteSpace(ticket.Ticket));

        // 非法 Sha256（大写/短 hash）也必须回退普通票，不得命中。
        var invalid = await client.PostAsJsonAsync("/api/attachments/presign", new
        {
            contentType = "application/octet-stream",
            contentLength = 4,
            sha256 = "NOTHEX",
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        var invalidTicket = await invalid.Content.ReadFromJsonAsync<PresignDto>(WafTestHelpers.Json);
        Assert.False(invalidTicket!.Deduplicated);
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
        string AttachmentId, string UploadUrl, string DownloadPath, string PublicUrl, string ObjectKey, string Ticket, DateTimeOffset ExpiresAt, bool Deduplicated);

    private sealed record ConfirmDto(
        string AttachmentId, string DownloadPath, string PublicUrl, string ObjectKey, string Status);

    private sealed class FakeAttachmentMetadataStore(IReadOnlyList<AttachmentRecord> rows) : IAttachmentMetadataStore
    {
        public bool IsAvailable => true;
        public string UnavailableReason => string.Empty;

        public Task<AttachmentDedupCandidate?> TryFindDedupCandidateAsync(
            long uploaderUserId, string sha256Hex, CancellationToken cancellationToken = default)
            => Task.FromResult<AttachmentDedupCandidate?>(null);

        public Task<Core.Models.Attachment.AttachmentUploadReservationStatus> ReserveTicketedAsync(
            string attachmentId, long uploaderUserId, string objectKey, string? publicUrl,
            string contentType, long sizeBytes, string? originalName, string? clientAttachmentId = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Core.Models.Attachment.AttachmentUploadReservationStatus.Reserved);

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

        public Task<IReadOnlyList<AttachmentAbandonBatchItem>> AbandonAgedUnboundAsync(
            TimeSpan maxAge,
            int batchSize,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AttachmentAbandonBatchItem>>([]);

        public Task<AttachmentOpsOrphanQueryResult> QueryOpsOrphansAsync(
            TimeSpan orphanAge,
            TimeSpan stuckScanningAge,
            int sampleLimit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AttachmentOpsOrphanQueryResult(
                Available: true,
                UnavailableReason: null,
                ConfirmedUnboundPastAgeCount: 0,
                AbandonedUploadingPastAgeCount: 0,
                StuckScanningCount: 0,
                OldestConfirmedUnboundAtMs: null,
                OldestUploadingAtMs: null,
                OldestStuckScanningAtMs: null,
                ActiveAttachmentCount: rows.Count,
                ActiveSizeBytesSum: rows.Sum(r => r.SizeBytes),
                WorstConfirmedUnbound: [],
                WorstUploading: [],
                WorstStuckScanning: []));
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
