using System.Net;
using System.Net.Http.Json;
using System.Text;
using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

/// <summary>
/// 附件断点续传上传集成测试（Local 存储路径）：
/// 分块顺序追加 → received 递增 → 定稿 confirm/下载一致；
/// offset 错位返回服务端权威 received；累计超 MaxBytes 删 partial 且票仍可用；
/// 整包 PUT 与分块混用兼容；progress 探针覆盖上传中/完成后/无效票。
/// </summary>
[Collection(nameof(RedisPostgresCollection))]
public sealed class AttachmentResumableUploadTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [SkippableFact]
    public async Task ChunkedUpload_ThreeAppends_Completes_ConfirmAndDownloadMatches()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await RealtimeAttachmentTestSchema.EnsureAsync(postgres.ConnectionString);
        var attachmentRoot = Path.Combine(Path.GetTempPath(), "chatapp-resume-att", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachmentRoot);

        await using var factory = CreateFactory(attachmentRoot);
        var client = await CreateLoggedInClientAsync(factory, "resume");
        var payload = Encoding.UTF8.GetBytes("resumable-upload-payload-0123456789");
        var ticket = await PresignAsync(client, payload.Length);

        // 顺序分块追加（10 字节/块）：received 单调递增，最后一块触发定稿。
        var received = 0L;
        const int chunkSize = 10;
        for (var start = 0; start < payload.Length; start += chunkSize)
        {
            var len = Math.Min(chunkSize, payload.Length - start);
            var isLast = start + len >= payload.Length;
            var append = await AppendChunkAsync(client, ticket.Ticket, received, payload, start, len);
            Assert.Equal(HttpStatusCode.OK, append.StatusCode);
            var body = await append.Content.ReadFromJsonAsync<ChunkDto>(WafTestHelpers.Json);
            received = body!.Received;
            Assert.Equal(start + len, received);
            Assert.Equal(isLast, body.Completed);
        }

        // 定稿后 progress 探针返回最终长度。
        var progress = await client.GetAsync($"/api/attachments/upload/progress?ticket={Uri.EscapeDataString(ticket.Ticket)}");
        Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
        Assert.Equal(payload.Length, (await progress.Content.ReadFromJsonAsync<ChunkDto>(WafTestHelpers.Json))!.Received);

        var confirm = await ConfirmAsync(client, ticket);
        Assert.Equal(HttpStatusCode.Accepted, confirm.StatusCode);
        await ProcessScanAsync(factory);

        Assert.Equal(payload, await DownloadAsync(client, ticket.AttachmentId));
    }

    [SkippableFact]
    public async Task ChunkedUpload_OffsetMismatch_ReturnsAuthoritativeReceived_AndAligns()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await RealtimeAttachmentTestSchema.EnsureAsync(postgres.ConnectionString);
        var attachmentRoot = Path.Combine(Path.GetTempPath(), "chatapp-resume-off", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachmentRoot);

        await using var factory = CreateFactory(attachmentRoot);
        var client = await CreateLoggedInClientAsync(factory, "resumeoff");
        var payload = Encoding.UTF8.GetBytes("offset-mismatch-payload");
        var ticket = await PresignAsync(client, payload.Length);

        Assert.Equal(HttpStatusCode.OK, (await AppendChunkAsync(client, ticket.Ticket, 0, payload, 0, 5)).StatusCode);

        // 错位续传：offset=3 != 服务端 5 → 400 + 权威 received=5。
        var mismatch = await AppendChunkAsync(client, ticket.Ticket, 3, payload, 5, 5);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        var err = await mismatch.Content.ReadFromJsonAsync<ChunkDto>(WafTestHelpers.Json);
        Assert.Equal(5, err!.Received);

        // 客户端按权威值对齐后续传 → 正常定稿。
        var aligned = await AppendChunkAsync(client, ticket.Ticket, 5, payload, 5, payload.Length - 5);
        Assert.Equal(HttpStatusCode.OK, aligned.StatusCode);
        var done = await aligned.Content.ReadFromJsonAsync<ChunkDto>(WafTestHelpers.Json);
        Assert.True(done!.Completed);
        Assert.Equal(payload.Length, done.Received);

        Assert.Equal(HttpStatusCode.Accepted, (await ConfirmAsync(client, ticket)).StatusCode);
        await ProcessScanAsync(factory);
        Assert.Equal(payload, await DownloadAsync(client, ticket.AttachmentId));
    }

    [SkippableFact]
    public async Task ChunkedUpload_ExceedsMaxBytes_DeletesPartial_TicketStillUsable()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await RealtimeAttachmentTestSchema.EnsureAsync(postgres.ConnectionString);
        var attachmentRoot = Path.Combine(Path.GetTempPath(), "chatapp-resume-max", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachmentRoot);

        // MaxBytes=16：第二块使累计超过上限。声明 16（== MaxBytes），首块 8 字节不触发定稿。
        await using var factory = CreateFactory(attachmentRoot, maxBytes: 16);
        var client = await CreateLoggedInClientAsync(factory, "resumemax");
        var payload = Encoding.UTF8.GetBytes("0123456789ABCDEFGH"); // 18 字节
        var ticket = await PresignAsync(client, 16);

        Assert.Equal(HttpStatusCode.OK, (await AppendChunkAsync(client, ticket.Ticket, 0, payload, 0, 8)).StatusCode);

        // 累计 8+10 > 16 → 400，partial 被删除。
        var over = await AppendChunkAsync(client, ticket.Ticket, 8, payload, 8, 10);
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        var err = await over.Content.ReadFromJsonAsync<ChunkDto>(WafTestHelpers.Json);
        Assert.Equal("附件大小超限", err!.Message);

        // partial 已删除：权威 received 回到 0；票未消费仍可用。
        var progress = await client.GetAsync($"/api/attachments/upload/progress?ticket={Uri.EscapeDataString(ticket.Ticket)}");
        Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
        Assert.Equal(0, (await progress.Content.ReadFromJsonAsync<ChunkDto>(WafTestHelpers.Json))!.Received);

        // 从头重新分块可完整走完并确认。
        Assert.Equal(HttpStatusCode.OK, (await AppendChunkAsync(client, ticket.Ticket, 0, payload, 0, 8)).StatusCode);
        var tail = await AppendChunkAsync(client, ticket.Ticket, 8, payload, 8, 8);
        Assert.True((await tail.Content.ReadFromJsonAsync<ChunkDto>(WafTestHelpers.Json))!.Completed);
        Assert.Equal(HttpStatusCode.Accepted, (await ConfirmAsync(client, ticket)).StatusCode);
        await ProcessScanAsync(factory);
        Assert.Equal(payload[..16], await DownloadAsync(client, ticket.AttachmentId));
    }

    [SkippableFact]
    public async Task WholeFileUpload_AfterPartialChunks_StillWorks()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await RealtimeAttachmentTestSchema.EnsureAsync(postgres.ConnectionString);
        var attachmentRoot = Path.Combine(Path.GetTempPath(), "chatapp-resume-whole", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachmentRoot);

        await using var factory = CreateFactory(attachmentRoot);
        var client = await CreateLoggedInClientAsync(factory, "resumewhole");
        var payload = Encoding.UTF8.GetBytes("whole-file-after-chunks");
        var ticket = await PresignAsync(client, payload.Length);

        // 先追加分块 partial，再用整包 PUT（应重写 partial 一次成功）。
        Assert.Equal(HttpStatusCode.OK, (await AppendChunkAsync(client, ticket.Ticket, 0, payload, 0, 5)).StatusCode);

        using (var content = new ByteArrayContent(payload))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            var upload = await client.PutAsync(
                $"/api/attachments/upload?ticket={Uri.EscapeDataString(ticket.Ticket)}", content);
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
            var body = await upload.Content.ReadFromJsonAsync<ChunkDto>(WafTestHelpers.Json);
            Assert.Equal(payload.Length, body!.Received);
        }

        Assert.Equal(HttpStatusCode.Accepted, (await ConfirmAsync(client, ticket)).StatusCode);
        await ProcessScanAsync(factory);
        Assert.Equal(payload, await DownloadAsync(client, ticket.AttachmentId));
    }

    [SkippableFact]
    public async Task ProgressProbe_BeforeDuringAfter_AndInvalidTicket()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await RealtimeAttachmentTestSchema.EnsureAsync(postgres.ConnectionString);
        var attachmentRoot = Path.Combine(Path.GetTempPath(), "chatapp-resume-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachmentRoot);

        await using var factory = CreateFactory(attachmentRoot);
        var client = await CreateLoggedInClientAsync(factory, "resumeprobe");
        var payload = Encoding.UTF8.GetBytes("progress-probe-bytes");
        var ticket = await PresignAsync(client, payload.Length);

        // 上传前：received = 0。
        var before = await client.GetAsync($"/api/attachments/upload/progress?ticket={Uri.EscapeDataString(ticket.Ticket)}");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(0, (await before.Content.ReadFromJsonAsync<ChunkDto>(WafTestHelpers.Json))!.Received);

        Assert.Equal(HttpStatusCode.OK, (await AppendChunkAsync(client, ticket.Ticket, 0, payload, 0, 7)).StatusCode);
        var during = await client.GetAsync($"/api/attachments/upload/progress?ticket={Uri.EscapeDataString(ticket.Ticket)}");
        Assert.Equal(HttpStatusCode.OK, during.StatusCode);
        Assert.Equal(7, (await during.Content.ReadFromJsonAsync<ChunkDto>(WafTestHelpers.Json))!.Received);

        Assert.Equal(HttpStatusCode.OK, (await AppendChunkAsync(client, ticket.Ticket, 7, payload, 7, payload.Length - 7)).StatusCode);
        var after = await client.GetAsync($"/api/attachments/upload/progress?ticket={Uri.EscapeDataString(ticket.Ticket)}");
        Assert.Equal(payload.Length, (await after.Content.ReadFromJsonAsync<ChunkDto>(WafTestHelpers.Json))!.Received);

        // 无效票 → 400。
        var invalidTicket = "deadbeef" + Guid.NewGuid().ToString("N");
        var invalid = await client.GetAsync($"/api/attachments/upload/progress?ticket={Uri.EscapeDataString(invalidTicket)}");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private async Task<HttpClient> CreateLoggedInClientAsync(ChatAppWebApplicationFactory factory, string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var db = postgres.CreateContext())
        {
            await WafTestHelpers.SeedUserAsync(db, $"{prefix}-{suffix}", $"{prefix}-{suffix}@ex.com", "Passw0rd!");
        }

        var client = factory.CreateClientWithDevice($"dev-{prefix}-{suffix}");
        var login = await WafTestHelpers.LoginAsync(client, $"{prefix}-{suffix}", "Passw0rd!");
        client.UseBearer(login.AccessToken!);
        return client;
    }

    private static async Task<PresignDto> PresignAsync(HttpClient client, int contentLength)
    {
        var presign = await client.PostAsJsonAsync("/api/attachments/presign", new
        {
            contentType = "application/octet-stream",
            contentLength,
            originalName = "resume.bin",
        }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, presign.StatusCode);
        return (await presign.Content.ReadFromJsonAsync<PresignDto>(WafTestHelpers.Json))!;
    }

    private static async Task<HttpResponseMessage> AppendChunkAsync(
        HttpClient client, string ticket, long offset, byte[] payload, int start, int length)
    {
        using var content = new ByteArrayContent(payload[start..(start + length)]);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        return await client.PutAsync(
            $"/api/attachments/upload?ticket={Uri.EscapeDataString(ticket)}&offset={offset}", content);
    }

    private static async Task<HttpResponseMessage> ConfirmAsync(HttpClient client, PresignDto ticket)
        => await client.PostAsJsonAsync("/api/attachments/confirm", new
        {
            objectKey = ticket.ObjectKey,
            ticket = ticket.Ticket,
            attachmentId = ticket.AttachmentId,
        }, WafTestHelpers.Json);

    private static async Task ProcessScanAsync(ChatAppWebApplicationFactory factory)
    {
        // 下载票要求附件离开 Scanning：处理后 Confirm 可下载。
        await using var scope = factory.Services.CreateAsyncScope();
        var scans = scope.ServiceProvider.GetRequiredService<IAttachmentScanService>();
        Assert.True(await scans.ProcessDueAsync() >= 1);
    }

    private static async Task<byte[]> DownloadAsync(HttpClient client, string attachmentId)
    {
        var mint = await client.PostAsync($"/api/attachments/{attachmentId}/ticket", content: null);
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);
        var ticket = await mint.Content.ReadFromJsonAsync<TicketDto>(WafTestHelpers.Json);
        var download = await client.GetAsync(
            $"/api/attachments/{attachmentId}/download?ticket={Uri.EscapeDataString(ticket!.Ticket)}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        return await download.Content.ReadAsByteArrayAsync();
    }

    private ChatAppWebApplicationFactory CreateFactory(string root, long maxBytes = 26_214_400)
        => new(
            postgres.ConnectionString,
            redis.ConnectionString,
            extraConfig: new Dictionary<string, string?>
            {
                ["AttachmentStorage:Provider"] = "Local",
                ["AttachmentStorage:LocalRootPath"] = root,
                ["AttachmentStorage:PublicBaseUrl"] = "/static/attachments",
                ["AttachmentStorage:MaxBytes"] = maxBytes.ToString(""),
                ["AttachmentStorage:TicketMinutes"] = "15",
                ["AttachmentStorage:AllowedContentTypes:0"] = "image/png",
                ["AttachmentStorage:AllowedContentTypes:1"] = "application/octet-stream",
                ["MessageEvidence:RealtimeConnectionString"] = postgres.ConnectionString,
                ["MessageEvidence:Schema"] = "realtime",
            });

    private sealed record PresignDto(
        string AttachmentId, string UploadUrl, string DownloadPath, string PublicUrl, string ObjectKey,
        string Ticket, DateTimeOffset ExpiresAt, bool Deduplicated);

    private sealed record TicketDto(string AttachmentId, string Ticket, DateTimeOffset ExpiresAt, string DownloadUrl);

    private sealed record ChunkDto(long Received, bool Completed = false, string? Message = null);
}
