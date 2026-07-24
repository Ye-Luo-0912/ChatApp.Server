using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
using Core.Settings;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

/// <summary>S3 签名下载路径可单元测试（假存储，不连真 S3）。</summary>
public sealed class AttachmentSignedDownloadTests
{
    [Fact]
    public async Task FakeS3Storage_ReturnsShortLivedSignedUrl()
    {
        var storage = new FakeS3AttachmentStorage();
        var signed = await storage.CreateSignedDownloadUrlAsync(
            "attachments/9/abc.png",
            ttl: TimeSpan.FromMinutes(3));

        Assert.NotNull(signed);
        Assert.StartsWith("https://signed.example/", signed!.Url, StringComparison.Ordinal);
        Assert.Contains("attachments/9/abc.png", signed.Url, StringComparison.Ordinal);
        Assert.True(signed.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(signed.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task LocalStorage_OpenRead_StreamsFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "chatapp-att-signed", Guid.NewGuid().ToString("N"));
        var key = "1/file.bin";
        var full = Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var expected = "hello-attach"u8.ToArray();
        await File.WriteAllBytesAsync(full, expected);

        var storage = new LocalAttachmentStorage(
            Options.Create(new AttachmentStorageOptions
            {
                LocalRootPath = root,
                UsePublicStatic = false,
            }),
            new NoopCacheProvider(),
            NullLogger<LocalAttachmentStorage>.Instance);

        Assert.Null(await storage.CreateSignedDownloadUrlAsync(key));
        var read = await storage.OpenReadAsync(key);
        Assert.NotNull(read);
        await using (read!.Content)
        {
            using var ms = new MemoryStream();
            await read.Content.CopyToAsync(ms);
            Assert.Equal(expected, ms.ToArray());
        }
    }

    private sealed class FakeS3AttachmentStorage : IAttachmentStorage
    {
        public bool IsAllowedContentType(string contentType) => true;
        public long MaxBytes => 1;

        public Task<(string AttachmentId, string ObjectKey, string Ticket, string UploadUrl, string PublicUrl, DateTimeOffset ExpiresAt)>
            CreateUploadTicketAsync(
                long userId, string contentType, long contentLength,
                string? originalName = null, string? clientAttachmentId = null,
                CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? AttachmentId, long SizeBytes, string? Sha256Hex, string? Error)> StoreAsync(
            long userId, string ticket, Stream content, string contentType, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? AttachmentId, string? ContentType, long SizeBytes, string? OriginalName, string? Error)>
            ConfirmObjectAsync(
                long userId, string objectKey, string? ticket = null, string? attachmentId = null,
                CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public string? TryResolveLocalPhysicalPath(string objectKey) => null;

        public Task<AttachmentReadResult?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
            => Task.FromResult<AttachmentReadResult?>(null);

        public Task<AttachmentSignedUrl?> CreateSignedDownloadUrlAsync(
            string objectKey, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            var expires = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(5));
            return Task.FromResult<AttachmentSignedUrl?>(
                new AttachmentSignedUrl($"https://signed.example/{objectKey}?exp={expires.ToUnixTimeSeconds()}", expires));
        }

        public Task DeleteAsync(string objectKeyOrUrl, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task TryDeleteAsync(string? objectKeyOrUrl, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
