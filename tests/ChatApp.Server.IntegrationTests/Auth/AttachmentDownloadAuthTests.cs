using System.Net;
using System.Net.Http.Json;
using System.Text;
using ChatApp.Server.IntegrationTests.Support;
using Core.Models.Export;
using Npgsql;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisPostgresCollection))]
public sealed class AttachmentDownloadAuthTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [SkippableFact]
    public async Task Download_Anonymous_Returns401()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync($"/api/attachments/{Guid.NewGuid():N}/download");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Download_NonMember_Returns403_Member_Returns200()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await EnsureSchemaAsync(postgres.ConnectionString);
        var attachmentRoot = Path.Combine(Path.GetTempPath(), "chatapp-dl-att", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachmentRoot);

        await using var factory = CreateFactory(attachmentRoot);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        long ownerId, outsiderId;
        await using (var db = postgres.CreateContext())
        {
            var owner = await WafTestHelpers.SeedUserAsync(db, $"own-{suffix}", $"own-{suffix}@ex.com", "Passw0rd!");
            var outsider = await WafTestHelpers.SeedUserAsync(db, $"out-{suffix}", $"out-{suffix}@ex.com", "Passw0rd!");
            ownerId = owner.Id;
            outsiderId = outsider.Id;
        }

        using var ownerClient = factory.CreateClientWithDevice($"dev-own-{suffix}");
        var ownerLogin = await WafTestHelpers.LoginAsync(ownerClient, $"own-{suffix}", "Passw0rd!");
        ownerClient.UseBearer(ownerLogin.AccessToken!);

        var payload = Encoding.UTF8.GetBytes("download-auth-bytes");
        var ticket = await PresignUploadConfirmAsync(ownerClient, payload, "photo.png", "image/png");

        // Bound + conversation membership for owner; outsider not a member.
        var conversationId = $"c-{suffix}";
        await using (var conn = new NpgsqlConnection(postgres.ConnectionString))
        {
            await conn.OpenAsync();
            await using (var cmd = new NpgsqlCommand(
                             """
                             INSERT INTO realtime.conversation_members (conversation_id, user_id, joined_at_ms)
                             VALUES (@cid, @uid, @ms)
                             ON CONFLICT DO NOTHING;
                             UPDATE realtime.attachments
                             SET status = @bound, conversation_id = @cid, message_id = @mid, bound_at_ms = @ms
                             WHERE attachment_id = @aid;
                             """, conn))
            {
                cmd.Parameters.AddWithValue("cid", conversationId);
                cmd.Parameters.AddWithValue("uid", ownerId);
                cmd.Parameters.AddWithValue("ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("bound", (short)AttachmentStatus.Bound);
                cmd.Parameters.AddWithValue("mid", $"m-{suffix}");
                cmd.Parameters.AddWithValue("aid", ticket.AttachmentId);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        using var outsiderClient = factory.CreateClientWithDevice($"dev-out-{suffix}");
        var outsiderLogin = await WafTestHelpers.LoginAsync(outsiderClient, $"out-{suffix}", "Passw0rd!");
        outsiderClient.UseBearer(outsiderLogin.AccessToken!);

        var forbidden = await outsiderClient.GetAsync($"/api/attachments/{ticket.AttachmentId}/download");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var ok = await ownerClient.GetAsync($"/api/attachments/{ticket.AttachmentId}/download");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var bytes = await ok.Content.ReadAsByteArrayAsync();
        Assert.Equal(payload, bytes);

        // Confirmed unbound：仅上传者可下
        var unbound = await PresignUploadConfirmAsync(ownerClient, Encoding.UTF8.GetBytes("unbound"), "note.bin", "application/octet-stream");
        var unboundOk = await ownerClient.GetAsync($"/api/attachments/{unbound.AttachmentId}/download");
        Assert.Equal(HttpStatusCode.OK, unboundOk.StatusCode);

        var unboundForbidden = await outsiderClient.GetAsync($"/api/attachments/{unbound.AttachmentId}/download");
        Assert.Equal(HttpStatusCode.Forbidden, unboundForbidden.StatusCode);

        _ = outsiderId;
    }

    [SkippableFact]
    public async Task Download_RangeRequest_Returns206PartialContent()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await EnsureSchemaAsync(postgres.ConnectionString);
        await using var factory = CreateFactory();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        long ownerId;
        await using (var db = postgres.CreateContext())
        {
            var owner = await WafTestHelpers.SeedUserAsync(db, $"rng-{suffix}", $"rng-{suffix}@ex.com", "Passw0rd!");
            ownerId = owner.Id;
        }

        using var ownerClient = factory.CreateClientWithDevice($"dev-rng-{suffix}");
        var ownerLogin = await WafTestHelpers.LoginAsync(ownerClient, $"rng-{suffix}", "Passw0rd!");
        ownerClient.UseBearer(ownerLogin.AccessToken!);

        var payload = Encoding.UTF8.GetBytes("0123456789ABCDEF");
        var ticket = await PresignUploadConfirmAsync(ownerClient, payload, "range.bin", "application/octet-stream");

        var conversationId = $"c-rng-{suffix}";
        await using (var conn = new NpgsqlConnection(postgres.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO realtime.conversation_members (conversation_id, user_id, joined_at_ms)
                VALUES (@cid, @uid, @ms)
                ON CONFLICT DO NOTHING;
                UPDATE realtime.attachments
                SET status = @bound, conversation_id = @cid, message_id = @mid, bound_at_ms = @ms
                WHERE attachment_id = @aid;
                """, conn);
            cmd.Parameters.AddWithValue("cid", conversationId);
            cmd.Parameters.AddWithValue("uid", ownerId);
            cmd.Parameters.AddWithValue("ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("bound", (short)AttachmentStatus.Bound);
            cmd.Parameters.AddWithValue("mid", $"m-rng-{suffix}");
            cmd.Parameters.AddWithValue("aid", ticket.AttachmentId);
            await cmd.ExecuteNonQueryAsync();
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/attachments/{ticket.AttachmentId}/download");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(4, 9);

        var resp = await ownerClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal("456789"u8.ToArray(), bytes);
    }

    [SkippableFact]
    public async Task Abandon_UnboundAttachment_AllowsUploader_RejectsOutsider()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await EnsureSchemaAsync(postgres.ConnectionString);
        await using var factory = CreateFactory();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = postgres.CreateContext())
        {
            await WafTestHelpers.SeedUserAsync(db, $"abn-{suffix}", $"abn-{suffix}@ex.com", "Passw0rd!");
            await WafTestHelpers.SeedUserAsync(db, $"abx-{suffix}", $"abx-{suffix}@ex.com", "Passw0rd!");
        }

        using var ownerClient = factory.CreateClientWithDevice($"dev-abn-{suffix}");
        var ownerLogin = await WafTestHelpers.LoginAsync(ownerClient, $"abn-{suffix}", "Passw0rd!");
        ownerClient.UseBearer(ownerLogin.AccessToken!);

        using var outsiderClient = factory.CreateClientWithDevice($"dev-abx-{suffix}");
        var outsiderLogin = await WafTestHelpers.LoginAsync(outsiderClient, $"abx-{suffix}", "Passw0rd!");
        outsiderClient.UseBearer(outsiderLogin.AccessToken!);

        var ticket = await PresignUploadConfirmAsync(
            ownerClient, Encoding.UTF8.GetBytes("abandon-me"), "gone.bin", "application/octet-stream");

        var forbidden = await outsiderClient.PostAsync(
            $"/api/attachments/{ticket.AttachmentId}/abandon", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var ok = await ownerClient.PostAsync(
            $"/api/attachments/{ticket.AttachmentId}/abandon", content: null);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var download = await ownerClient.GetAsync($"/api/attachments/{ticket.AttachmentId}/download");
        Assert.True(
            download.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"abandon 后下载应拒绝，实际 {download.StatusCode}");
    }

    private ChatAppWebApplicationFactory CreateFactory(string? attachmentRoot = null)
    {
        attachmentRoot ??= Path.Combine(Path.GetTempPath(), "chatapp-dl-att", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachmentRoot);
        return new ChatAppWebApplicationFactory(
            postgres.ConnectionString,
            redis.ConnectionString,
            extraConfig: new Dictionary<string, string?>
            {
                ["AttachmentStorage:Provider"] = "Local",
                ["AttachmentStorage:LocalRootPath"] = attachmentRoot,
                ["AttachmentStorage:UsePublicStatic"] = "false",
                ["AttachmentStorage:PublicBaseUrl"] = "/static/attachments",
                ["AttachmentStorage:MaxBytes"] = "26214400",
                ["AttachmentStorage:TicketMinutes"] = "15",
                ["AttachmentStorage:AllowedContentTypes:0"] = "image/png",
                ["AttachmentStorage:AllowedContentTypes:1"] = "application/octet-stream",
                ["MessageEvidence:RealtimeConnectionString"] = postgres.ConnectionString,
                ["MessageEvidence:Schema"] = "realtime",
            });
    }

    private static async Task<(string AttachmentId, string ObjectKey, string Ticket)> PresignUploadConfirmAsync(
        HttpClient client, byte[] payload, string originalName, string contentType)
    {
        var presign = await client.PostAsJsonAsync("/api/attachments/presign", new
        {
            contentType,
            contentLength = payload.Length,
            originalName,
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, presign.StatusCode);
        var ticket = await presign.Content.ReadFromJsonAsync<PresignDto>(WafTestHelpers.Json);
        Assert.NotNull(ticket);

        using (var content = new ByteArrayContent(payload))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
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
        return (ticket.AttachmentId, ticket.ObjectKey, ticket.Ticket);
    }

    private static async Task EnsureSchemaAsync(string connectionString)
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
            CREATE TABLE IF NOT EXISTS realtime.conversation_members (
                conversation_id varchar(64) NOT NULL,
                user_id         bigint      NOT NULL,
                joined_at_ms    bigint      NOT NULL,
                PRIMARY KEY (conversation_id, user_id)
            );
            CREATE TABLE IF NOT EXISTS realtime.messages (
                message_id      varchar(64) PRIMARY KEY,
                sender_user_id  bigint NOT NULL,
                receiver_user_id bigint NOT NULL,
                conversation_id varchar(64) NULL,
                content         text NULL,
                created_at_ms   bigint NOT NULL DEFAULT 0
            );
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed record PresignDto(
        string AttachmentId, string UploadUrl, string DownloadPath, string ObjectKey, string Ticket);
}
