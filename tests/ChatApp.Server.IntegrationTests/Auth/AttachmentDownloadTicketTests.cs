using System.Net;
using System.Net.Http.Json;
using System.Text;
using ChatApp.Server.IntegrationTests.Support;
using Core.Caching;
using Core.Interfaces.Cache;
using Core.Models.Export;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisPostgresCollection))]
public sealed class AttachmentDownloadTicketTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [SkippableFact]
    public async Task ValidTicket_AllowsDownload_WrongUser_Rejected_Expired_Rejected()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await RealtimeAttachmentTestSchema.EnsureAsync(postgres.ConnectionString);
        var root = Path.Combine(Path.GetTempPath(), "chatapp-dl-ticket", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        await using var factory = CreateFactory(root);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = postgres.CreateContext())
        {
            await WafTestHelpers.SeedUserAsync(db, $"tk-a-{suffix}", $"tk-a-{suffix}@ex.com", "Passw0rd!");
            await WafTestHelpers.SeedUserAsync(db, $"tk-b-{suffix}", $"tk-b-{suffix}@ex.com", "Passw0rd!");
        }

        using var owner = factory.CreateClientWithDevice($"dev-tka-{suffix}");
        var ownerLogin = await WafTestHelpers.LoginAsync(owner, $"tk-a-{suffix}", "Passw0rd!");
        owner.UseBearer(ownerLogin.AccessToken!);

        using var other = factory.CreateClientWithDevice($"dev-tkb-{suffix}");
        var otherLogin = await WafTestHelpers.LoginAsync(other, $"tk-b-{suffix}", "Passw0rd!");
        other.UseBearer(otherLogin.AccessToken!);

        var payload = Encoding.UTF8.GetBytes("ticket-download-bytes");
        var attachmentId = await PresignUploadConfirmScanAsync(factory, owner, payload);

        var mint = await owner.PostAsync($"/api/attachments/{attachmentId}/ticket", content: null);
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);
        var ticketBody = await mint.Content.ReadFromJsonAsync<TicketDto>(WafTestHelpers.Json);
        Assert.False(string.IsNullOrWhiteSpace(ticketBody?.Ticket));
        Assert.Contains("ticket=", ticketBody!.DownloadUrl, StringComparison.Ordinal);

        // 他人持有同 ticket → InvalidTicket
        var wrongUser = await other.GetAsync(
            $"/api/attachments/{attachmentId}/download?ticket={Uri.EscapeDataString(ticketBody.Ticket)}");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongUser.StatusCode);

        // 错误用户只会读取绑定信息，不消费原票；票仍可由 owner 使用。
        var ok = await owner.GetAsync(
            $"/api/attachments/{attachmentId}/download?ticket={Uri.EscapeDataString(ticketBody.Ticket)}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(payload, await ok.Content.ReadAsByteArrayAsync());

        // 单次消费后再用 → 无效
        var reused = await owner.GetAsync(
            $"/api/attachments/{attachmentId}/download?ticket={Uri.EscapeDataString(ticketBody.Ticket)}");
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);

        // 过期票
        var mint3 = await owner.PostAsync($"/api/attachments/{attachmentId}/ticket", content: null);
        var ticket3 = await mint3.Content.ReadFromJsonAsync<TicketDto>(WafTestHelpers.Json);
        Assert.NotNull(ticket3);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var cache = scope.ServiceProvider.GetRequiredService<ICacheValueStore>();
            await cache.RemoveAsync(CacheConstants.AttachmentDownloadTicketPrefix + ticket3!.Ticket);
        }

        var expired = await owner.GetAsync(
            $"/api/attachments/{attachmentId}/download?ticket={Uri.EscapeDataString(ticket3.Ticket)}");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [SkippableFact]
    public async Task Ticket_WhileScanning_Returns409()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await RealtimeAttachmentTestSchema.EnsureAsync(postgres.ConnectionString);
        var root = Path.Combine(Path.GetTempPath(), "chatapp-dl-ticket-scan", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var factory = CreateFactory(root);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = postgres.CreateContext())
        {
            await WafTestHelpers.SeedUserAsync(db, $"tks-{suffix}", $"tks-{suffix}@ex.com", "Passw0rd!");
        }

        using var client = factory.CreateClientWithDevice($"dev-tks-{suffix}");
        var login = await WafTestHelpers.LoginAsync(client, $"tks-{suffix}", "Passw0rd!");
        client.UseBearer(login.AccessToken!);

        var payload = Encoding.UTF8.GetBytes("still-scanning");
        var presign = await client.PostAsJsonAsync("/api/attachments/presign", new
        {
            contentType = "application/octet-stream",
            contentLength = payload.Length,
            originalName = "x.bin",
        }, WafTestHelpers.Json);
        var ticket = await presign.Content.ReadFromJsonAsync<PresignDto>(WafTestHelpers.Json);
        using (var content = new ByteArrayContent(payload))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            await client.PutAsync($"/api/attachments/upload?ticket={Uri.EscapeDataString(ticket!.Ticket)}", content);
        }

        var mint = await client.PostAsync($"/api/attachments/{ticket.AttachmentId}/ticket", content: null);
        Assert.Equal(HttpStatusCode.Conflict, mint.StatusCode);
    }

    private ChatAppWebApplicationFactory CreateFactory(string root) =>
        new(
            postgres.ConnectionString,
            redis.ConnectionString,
            extraConfig: new Dictionary<string, string?>
            {
                ["AttachmentStorage:Provider"] = "Local",
                ["AttachmentStorage:LocalRootPath"] = root,
                ["AttachmentStorage:DownloadTicketMinutes"] = "2",
                ["AttachmentStorage:AllowedContentTypes:0"] = "application/octet-stream",
                ["AttachmentStorage:AllowedContentTypes:1"] = "image/png",
                ["MessageEvidence:RealtimeConnectionString"] = postgres.ConnectionString,
                ["MessageEvidence:Schema"] = "realtime",
            });

    private static async Task<string> PresignUploadConfirmScanAsync(
        ChatAppWebApplicationFactory factory, HttpClient client, byte[] payload)
    {
        var presign = await client.PostAsJsonAsync("/api/attachments/presign", new
        {
            contentType = "application/octet-stream",
            contentLength = payload.Length,
            originalName = "t.bin",
        }, WafTestHelpers.Json);
        var ticket = await presign.Content.ReadFromJsonAsync<PresignDto>(WafTestHelpers.Json);
        using (var content = new ByteArrayContent(payload))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            await client.PutAsync($"/api/attachments/upload?ticket={Uri.EscapeDataString(ticket!.Ticket)}", content);
        }

        var confirm = await client.PostAsJsonAsync("/api/attachments/confirm", new
        {
            objectKey = ticket.ObjectKey,
            ticket = ticket.Ticket,
            attachmentId = ticket.AttachmentId,
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.Accepted, confirm.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var scans = scope.ServiceProvider.GetRequiredService<Core.Interfaces.IAttachmentScanService>();
        Assert.True(await scans.ProcessDueAsync() >= 1);
        return ticket.AttachmentId;
    }

    private sealed record PresignDto(
        string AttachmentId, string UploadUrl, string DownloadPath, string ObjectKey, string Ticket);

    private sealed record TicketDto(
        string AttachmentId, string Ticket, DateTimeOffset ExpiresAt, string DownloadUrl);
}
