using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ChatApp.Server.IntegrationTests.Support;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Http;

[Collection(nameof(RedisPostgresCollection))]
public sealed class AttachmentUploadSizeTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    private const int MaxBytes = 25 * 1024 * 1024;
    private const int EndpointLimit = 30 * 1024 * 1024;

    [SkippableFact]
    public async Task Upload_4MB_Succeeds()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);
        var (client, factory) = await CreateAuthenticatedClientAsync();
        await using (factory)
        {
            var payload = new byte[4 * 1024 * 1024];
            var ticket = await PresignAsync(client, payload.Length);
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var res = await client.PutAsync($"/api/attachments/upload?ticket={Uri.EscapeDataString(ticket)}", content);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Upload_AtMaxBytes_Succeeds()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);
        var (client, factory) = await CreateAuthenticatedClientAsync();
        await using (factory)
        {
            var payload = new byte[MaxBytes];
            var ticket = await PresignAsync(client, MaxBytes);
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var res = await client.PutAsync($"/api/attachments/upload?ticket={Uri.EscapeDataString(ticket)}", content);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
    }
    [SkippableFact]
    public async Task Upload_OverMaxBytes_UnderEndpointLimit_RejectedByService()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);
        var (client, factory) = await CreateAuthenticatedClientAsync();
        await using (factory)
        {
            var payload = new byte[MaxBytes + 1];
            var ticket = await PresignAsync(client, MaxBytes);
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var res = await client.PutAsync($"/api/attachments/upload?ticket={Uri.EscapeDataString(ticket)}", content);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Upload_OverEndpointLimit_RejectedByMiddleware()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var payload = new byte[EndpointLimit + 1];
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var res = await client.PutAsync("/api/attachments/upload?ticket=deadbeef", content);
        Assert.True(
            res.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest,
            $"expected 413/400 but got {res.StatusCode}");
    }
    [SkippableFact]
    public async Task Upload_NoContentLength_Chunked_NotRejectedByMiddleware()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);
        var (client, factory) = await CreateAuthenticatedClientAsync();
        await using (factory)
        {
            var payload = new byte[1024];
            var ticket = await PresignAsync(client, payload.Length);
            using var content = new ChunkedContent(payload, "application/octet-stream");
            var res = await client.PutAsync($"/api/attachments/upload?ticket={Uri.EscapeDataString(ticket)}", content);
            Assert.True(
                res.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest,
                $"expected 200/400 but got {res.StatusCode}");
        }
    }

    private ChatAppWebApplicationFactory CreateFactory()
    {
        var attachmentRoot = Path.Combine(Path.GetTempPath(), "chatapp-waf-size", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachmentRoot);
        return new ChatAppWebApplicationFactory(
            postgres.ConnectionString,
            redis.ConnectionString,
            extraConfig: new Dictionary<string, string?>
            {
                ["AttachmentStorage:Provider"] = "Local",
                ["AttachmentStorage:LocalRootPath"] = attachmentRoot,
                ["AttachmentStorage:PublicBaseUrl"] = "/static/attachments",
                ["AttachmentStorage:MaxBytes"] = MaxBytes.ToString(),
                ["AttachmentStorage:TicketMinutes"] = "15",
                ["AttachmentStorage:AllowedContentTypes:0"] = "image/png",
                ["AttachmentStorage:AllowedContentTypes:1"] = "application/octet-stream",
                ["MessageEvidence:RealtimeConnectionString"] = postgres.ConnectionString,
                ["MessageEvidence:Schema"] = "realtime",
            });
    }
    private async Task<(HttpClient Client, ChatAppWebApplicationFactory Factory)> CreateAuthenticatedClientAsync()
    {
        await RealtimeAttachmentTestSchema.EnsureAsync(postgres.ConnectionString);
        var factory = CreateFactory();
        var client = factory.CreateClientWithDevice($"dev-size-{Guid.NewGuid():N}"[..24]);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var db = postgres.CreateContext())
            await WafTestHelpers.SeedUserAsync(db, $"sz-{suffix}", $"sz-{suffix}@ex.com", "Passw0rd!");
        var login = await WafTestHelpers.LoginAsync(client, $"sz-{suffix}", "Passw0rd!");
        client.UseBearer(login.AccessToken!);
        return (client, factory);
    }

    private static async Task<string> PresignAsync(HttpClient client, int contentLength)
    {
        var presign = await client.PostAsJsonAsync("/api/attachments/presign", new
        {
            contentType = "application/octet-stream",
            contentLength,
            originalName = "test.bin",
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, presign.StatusCode);
        var dto = await presign.Content.ReadFromJsonAsync<PresignDto>(WafTestHelpers.Json);
        Assert.False(string.IsNullOrWhiteSpace(dto?.Ticket));
        return dto!.Ticket;
    }
    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] _data;
        public ChunkedContent(byte[] data, string contentType)
        {
            _data = data;
            Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_data).AsTask();
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed record PresignDto(
        string AttachmentId, string UploadUrl, string DownloadPath, string PublicUrl,
        string ObjectKey, string Ticket, DateTimeOffset ExpiresAt);
}
