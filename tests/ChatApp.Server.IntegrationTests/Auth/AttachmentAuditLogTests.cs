using System.Net;
using System.Net.Http.Json;
using System.Text;
using ChatApp.Server.IntegrationTests.Support;
using Core.Caching;
using Core.Interfaces.Cache;
using Core.Models.Export;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

/// <summary>
/// 附件隔离审计（ACCOUNT-OPS-1）：用捕获 provider 证明审计事件随授权决策一起产出。
/// 决策码与端点行为本身由 AttachmentDownloadAuthTests / AttachmentDownloadTicketTests 覆盖；
/// 这里对代表性场景断言 4101（拒绝 Warning）/ 4110·4111（成功 Information）审计通路。
/// </summary>
[Collection(nameof(RedisPostgresCollection))]
public sealed class AttachmentAuditLogTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [SkippableFact]
    public async Task Download_ByOutsideMember_Emits4101Warning_WithAttachmentIdAndDecision()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await RealtimeAttachmentTestSchema.EnsureAsync(postgres.ConnectionString);
        var capture = new CapturingLoggerProvider();
        await using var factory = CreateFactory(capture);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        long ownerId;
        await using (var db = postgres.CreateContext())
        {
            var owner = await WafTestHelpers.SeedUserAsync(db, $"aud-o-{suffix}", $"aud-o-{suffix}@ex.com", "Passw0rd!");
            await WafTestHelpers.SeedUserAsync(db, $"aud-x-{suffix}", $"aud-x-{suffix}@ex.com", "Passw0rd!");
            ownerId = owner.Id;
        }

        using var ownerClient = factory.CreateClientWithDevice($"dev-aud-o-{suffix}");
        var ownerLogin = await WafTestHelpers.LoginAsync(ownerClient, $"aud-o-{suffix}", "Passw0rd!");
        ownerClient.UseBearer(ownerLogin.AccessToken!);

        var attachmentId = await PresignUploadConfirmAsync(
            factory, ownerClient, Encoding.UTF8.GetBytes("audit-forbidden"), "audit.png", "image/png");

        var conversationId = $"c-aud-{suffix}";
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
            cmd.Parameters.AddWithValue("mid", $"m-aud-{suffix}");
            cmd.Parameters.AddWithValue("aid", attachmentId);
            await cmd.ExecuteNonQueryAsync();
        }

        using var outsiderClient = factory.CreateClientWithDevice($"dev-aud-x-{suffix}");
        var outsiderLogin = await WafTestHelpers.LoginAsync(outsiderClient, $"aud-x-{suffix}", "Passw0rd!");
        outsiderClient.UseBearer(outsiderLogin.AccessToken!);

        var forbidden = await outsiderClient.GetAsync($"/api/attachments/{attachmentId}/download");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var denied = capture.Entries.SingleOrDefault(e =>
            e.Level == LogLevel.Warning && e.EventId == 4101);
        Assert.NotNull(denied);
        Assert.Contains(attachmentId, denied!.Message, StringComparison.Ordinal);
        Assert.Contains("Action=download", denied.Message, StringComparison.Ordinal);
        Assert.Contains("Decision=forbidden", denied.Message, StringComparison.Ordinal);
        // Owner 自己的成功下载不应产生 4101 拒绝审计。
        Assert.Equal(1, capture.Entries.Count(e => e.EventId == 4101));
    }

    [SkippableFact]
    public async Task TicketIssued_Confirmed_ExpiredTicket_EmitAuditEvents()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await RealtimeAttachmentTestSchema.EnsureAsync(postgres.ConnectionString);
        var capture = new CapturingLoggerProvider();
        var root = Path.Combine(Path.GetTempPath(), "chatapp-aud-ticket", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var factory = CreateFactory(capture, root);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        long ownerId;
        await using (var db = postgres.CreateContext())
        {
            var owner = await WafTestHelpers.SeedUserAsync(db, $"aud-t-{suffix}", $"aud-t-{suffix}@ex.com", "Passw0rd!");
            ownerId = owner.Id;
        }

        using var ownerClient = factory.CreateClientWithDevice($"dev-aud-t-{suffix}");
        var ownerLogin = await WafTestHelpers.LoginAsync(ownerClient, $"aud-t-{suffix}", "Passw0rd!");
        ownerClient.UseBearer(ownerLogin.AccessToken!);

        var attachmentId = await PresignUploadConfirmAsync(
            factory, ownerClient, Encoding.UTF8.GetBytes("audit-ticket"), "audit-scan.bin", "application/octet-stream");

        // 4111 Confirm：Information，含 attachmentId 与 ownerId。
        var confirmed = capture.Entries.SingleOrDefault(e => e.Level == LogLevel.Information && e.EventId == 4111);
        Assert.NotNull(confirmed);
        Assert.Contains(attachmentId, confirmed!.Message, StringComparison.Ordinal);
        Assert.Contains($"OwnerId={ownerId}", confirmed.Message, StringComparison.Ordinal);

        // 4110 票据签发：Information，含 attachmentId 与 userId。
        var mint = await ownerClient.PostAsync($"/api/attachments/{attachmentId}/ticket", content: null);
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);
        var ticket = await mint.Content.ReadFromJsonAsync<TicketDto>(WafTestHelpers.Json);
        Assert.False(string.IsNullOrWhiteSpace(ticket?.Ticket));
        var issued = capture.Entries.SingleOrDefault(e => e.Level == LogLevel.Information && e.EventId == 4110);
        Assert.NotNull(issued);
        Assert.Contains(attachmentId, issued!.Message, StringComparison.Ordinal);
        Assert.Contains($"UserId={ownerId}", issued.Message, StringComparison.Ordinal);

        // 票据过期（删除缓存态）→ InvalidTicket 401 → 4101 Warning。
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var cache = scope.ServiceProvider.GetRequiredService<ICacheValueStore>();
            await cache.RemoveAsync(CacheConstants.AttachmentDownloadTicketPrefix + ticket!.Ticket);
        }

        var expired = await ownerClient.GetAsync(
            $"/api/attachments/{attachmentId}/download?ticket={Uri.EscapeDataString(ticket.Ticket)}");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);

        var denied = capture.Entries.SingleOrDefault(e =>
            e.Level == LogLevel.Warning && e.EventId == 4101);
        Assert.NotNull(denied);
        Assert.Contains(attachmentId, denied!.Message, StringComparison.Ordinal);
        Assert.Contains("Decision=invalid_ticket", denied.Message, StringComparison.Ordinal);
    }

    private ChatAppWebApplicationFactory CreateFactory(
        CapturingLoggerProvider capture,
        string? attachmentRoot = null)
    {
        attachmentRoot ??= Path.Combine(Path.GetTempPath(), "chatapp-aud-att", Guid.NewGuid().ToString("N"));
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
            },
            loggerProvider: capture);
    }

    private static async Task<string> PresignUploadConfirmAsync(
        ChatAppWebApplicationFactory factory,
        HttpClient client,
        byte[] payload,
        string originalName,
        string contentType)
    {
        var presign = await client.PostAsJsonAsync("/api/attachments/presign", new
        {
            contentType,
            contentLength = payload.Length,
            originalName,
        }, WafTestHelpers.Json);
        presign.EnsureSuccessStatusCode();
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
        Assert.Equal(HttpStatusCode.Accepted, confirm.StatusCode);
        await DrainScanJobsAsync(factory);
        return ticket.AttachmentId;
    }

    private static async Task DrainScanJobsAsync(ChatAppWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var scans = scope.ServiceProvider.GetRequiredService<Core.Interfaces.IAttachmentScanService>();
        for (var i = 0; i < 5; i++)
        {
            if (await scans.ProcessDueAsync() > 0)
                return;
        }
    }

    private sealed record PresignDto(
        string AttachmentId, string UploadUrl, string DownloadPath, string ObjectKey, string Ticket);

    private sealed record TicketDto(
        string AttachmentId, string Ticket, DateTimeOffset ExpiresAt, string DownloadUrl);
}
