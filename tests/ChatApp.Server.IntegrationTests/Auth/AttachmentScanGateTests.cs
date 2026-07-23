using System.Net;
using System.Net.Http.Json;
using System.Text;
using ChatApp.Server.IntegrationTests.Support;
using Core.Models.Export;
using Infrastructure.Services;
using Npgsql;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisPostgresCollection))]
public sealed class AttachmentScanGateTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [Fact]
    public void MagicSniffer_DetectsPngAndJpeg()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00 };
        Assert.Equal("image/png", AttachmentMagicSniffer.Sniff(png));

        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        Assert.Equal("image/jpeg", AttachmentMagicSniffer.Sniff(jpeg));

        Assert.Null(AttachmentMagicSniffer.Sniff("not-a-file"u8));
    }

    [SkippableFact]
    public async Task Download_WhileScanning_Returns409_Confirm_AllowsDownload_AndSniffSetsType()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await EnsureSchemaAsync(postgres.ConnectionString);
        var attachmentRoot = Path.Combine(Path.GetTempPath(), "chatapp-scan-att", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachmentRoot);

        await using var factory = CreateFactory(attachmentRoot);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = postgres.CreateContext())
        {
            await WafTestHelpers.SeedUserAsync(db, $"scan-{suffix}", $"scan-{suffix}@ex.com", "Passw0rd!");
        }

        using var client = factory.CreateClientWithDevice($"dev-scan-{suffix}");
        var login = await WafTestHelpers.LoginAsync(client, $"scan-{suffix}", "Passw0rd!");
        client.UseBearer(login.AccessToken!);

        // 最小 PNG 头 + 填充；客户端谎称 octet-stream
        var payload = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        };

        var presign = await client.PostAsJsonAsync("/api/attachments/presign", new
        {
            contentType = "application/octet-stream",
            contentLength = payload.Length,
            originalName = "shot.bin",
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, presign.StatusCode);
        var ticket = await presign.Content.ReadFromJsonAsync<PresignDto>(WafTestHelpers.Json);
        Assert.NotNull(ticket);

        using (var content = new ByteArrayContent(payload))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            var upload = await client.PutAsync(
                $"/api/attachments/upload?ticket={Uri.EscapeDataString(ticket!.Ticket)}", content);
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        }

        // Uploaded→Scanning：下载应 409
        var scanningDl = await client.GetAsync($"/api/attachments/{ticket.AttachmentId}/download");
        Assert.Equal(HttpStatusCode.Conflict, scanningDl.StatusCode);

        await using (var conn = new NpgsqlConnection(postgres.ConnectionString))
        {
            await conn.OpenAsync();
            await using var statusCmd = new NpgsqlCommand(
                "SELECT status FROM realtime.attachments WHERE attachment_id = @id", conn);
            statusCmd.Parameters.AddWithValue("id", ticket.AttachmentId);
            Assert.Equal((short)AttachmentStatus.Scanning, (short)(await statusCmd.ExecuteScalarAsync())!);
        }

        var confirm = await client.PostAsJsonAsync("/api/attachments/confirm", new
        {
            objectKey = ticket.ObjectKey,
            ticket = ticket.Ticket,
            attachmentId = ticket.AttachmentId,
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        await using (var conn = new NpgsqlConnection(postgres.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                """
                SELECT status, content_type FROM realtime.attachments WHERE attachment_id = @id
                """, conn);
            cmd.Parameters.AddWithValue("id", ticket.AttachmentId);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((short)AttachmentStatus.Confirmed, reader.GetInt16(0));
            Assert.Equal("image/png", reader.GetString(1));
        }

        var ok = await client.GetAsync($"/api/attachments/{ticket.AttachmentId}/download");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(payload, await ok.Content.ReadAsByteArrayAsync());
    }

    [SkippableFact]
    public async Task Confirm_RejectsDangerousExtension()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await EnsureSchemaAsync(postgres.ConnectionString);
        var attachmentRoot = Path.Combine(Path.GetTempPath(), "chatapp-scan-rej", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachmentRoot);

        await using var factory = CreateFactory(attachmentRoot);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var db = postgres.CreateContext())
        {
            await WafTestHelpers.SeedUserAsync(db, $"rej-{suffix}", $"rej-{suffix}@ex.com", "Passw0rd!");
        }

        using var client = factory.CreateClientWithDevice($"dev-rej-{suffix}");
        var login = await WafTestHelpers.LoginAsync(client, $"rej-{suffix}", "Passw0rd!");
        client.UseBearer(login.AccessToken!);

        var payload = Encoding.UTF8.GetBytes("MZ-not-really-but-name-matters");
        var presign = await client.PostAsJsonAsync("/api/attachments/presign", new
        {
            contentType = "application/octet-stream",
            contentLength = payload.Length,
            originalName = "malware.exe",
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, presign.StatusCode);
        var ticket = await presign.Content.ReadFromJsonAsync<PresignDto>(WafTestHelpers.Json);
        Assert.NotNull(ticket);

        using (var content = new ByteArrayContent(payload))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsync(
                $"/api/attachments/upload?ticket={Uri.EscapeDataString(ticket!.Ticket)}", content)).StatusCode);
        }

        var confirm = await client.PostAsJsonAsync("/api/attachments/confirm", new
        {
            objectKey = ticket.ObjectKey,
            ticket = ticket.Ticket,
            attachmentId = ticket.AttachmentId,
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);

        await using var conn = new NpgsqlConnection(postgres.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT status FROM realtime.attachments WHERE attachment_id = @id", conn);
        cmd.Parameters.AddWithValue("id", ticket.AttachmentId);
        Assert.Equal((short)AttachmentStatus.Rejected, (short)(await cmd.ExecuteScalarAsync())!);
    }

    private ChatAppWebApplicationFactory CreateFactory(string attachmentRoot) =>
        new(
            postgres.ConnectionString,
            redis.ConnectionString,
            extraConfig: new Dictionary<string, string?>
            {
                ["AttachmentStorage:Provider"] = "Local",
                ["AttachmentStorage:LocalRootPath"] = attachmentRoot,
                ["AttachmentStorage:MaxBytes"] = "26214400",
                ["AttachmentStorage:TicketMinutes"] = "15",
                ["AttachmentStorage:AllowedContentTypes:0"] = "image/png",
                ["AttachmentStorage:AllowedContentTypes:1"] = "image/jpeg",
                ["AttachmentStorage:AllowedContentTypes:2"] = "application/octet-stream",
                ["MessageEvidence:RealtimeConnectionString"] = postgres.ConnectionString,
                ["MessageEvidence:Schema"] = "realtime",
            });

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
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed record PresignDto(
        string AttachmentId, string UploadUrl, string DownloadPath, string PublicUrl, string ObjectKey, string Ticket, DateTimeOffset ExpiresAt);
}
