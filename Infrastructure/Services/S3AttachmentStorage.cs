using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>S3/MinIO 预签名附件上传；确认时校验对象并提升临时 .bin。</summary>
public sealed class S3AttachmentStorage : IAttachmentStorage, IDisposable
{
    private readonly AttachmentStorageOptions _options;
    private readonly ICacheProvider _cache;
    private readonly ILogger<S3AttachmentStorage> _logger;
    private readonly IAmazonS3 _s3;

    public S3AttachmentStorage(
        IOptions<AttachmentStorageOptions> options,
        ICacheProvider cache,
        ILogger<S3AttachmentStorage> logger)
    {
        _options = options.Value;
        _cache = cache;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.S3Bucket)
            || string.IsNullOrWhiteSpace(_options.S3AccessKey)
            || string.IsNullOrWhiteSpace(_options.S3SecretKey))
            throw new InvalidOperationException("AttachmentStorage S3 配置不完整");

        var config = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(_options.S3Region ?? "us-east-1"),
            ForcePathStyle = true,
        };
        if (!string.IsNullOrWhiteSpace(_options.S3Endpoint))
            config.ServiceURL = _options.S3Endpoint;

        _s3 = new AmazonS3Client(
            new BasicAWSCredentials(_options.S3AccessKey, _options.S3SecretKey),
            config);
    }

    public long MaxBytes => _options.MaxBytes;

    public bool IsAllowedContentType(string contentType) =>
        _options.AllowedContentTypes.Any(t =>
            string.Equals(t, contentType, StringComparison.OrdinalIgnoreCase));

    public async Task<(string AttachmentId, string ObjectKey, string Ticket, string UploadUrl, string PublicUrl, DateTimeOffset ExpiresAt)>
        CreateUploadTicketAsync(
            long userId,
            string contentType,
            long contentLength,
            string? originalName = null,
            string? clientAttachmentId = null,
            CancellationToken cancellationToken = default)
    {
        if (!IsAllowedContentType(contentType))
            throw new ArgumentException("不支持的附件格式");
        if (contentLength <= 0 || contentLength > MaxBytes)
            throw new ArgumentException($"附件大小须在 1~{MaxBytes} 字节之间");

        var attachmentId = Guid.NewGuid().ToString("N");
        var objectKey = $"attachments/{userId}/{attachmentId}.bin";
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var expires = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TicketMinutes, 1, 60));

        await _cache.SetStringPayloadAsync(
            $"attachment:ticket:{ticket}",
            new LocalAttachmentStorage.AttachmentTicketInfo(
                userId, attachmentId, objectKey, contentType, contentLength, originalName, clientAttachmentId),
            expires - DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.S3Bucket,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Expires = expires.UtcDateTime,
            ContentType = contentType,
        };
        var uploadUrl = await _s3.GetPreSignedURLAsync(request).ConfigureAwait(false);
        // 不再返回永久 PublicUrl；聊天侧走鉴权下载。
        return (attachmentId, objectKey, ticket, uploadUrl, string.Empty, expires);
    }

    public Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? AttachmentId, long SizeBytes, string? Sha256Hex, string? Error)> StoreAsync(
        long userId, string ticket, Stream content, string contentType, CancellationToken cancellationToken = default)
        => Task.FromResult<(bool, string?, string?, string?, long, string?, string?)>(
            (false, null, null, null, 0, null, "S3 模式请直传预签名 URL，再调用 confirm"));

    public async Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? AttachmentId, string? ContentType, long SizeBytes, string? OriginalName, string? Error)>
        ConfirmObjectAsync(
            long userId,
            string objectKey,
            string? ticket = null,
            string? attachmentId = null,
            CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return (false, null, null, null, null, 0, null, "确认附件须提供上传票");

        var ticketKey = $"attachment:ticket:{ticket}";
        var info = await _cache.TryGetAndDeleteStringPayloadAsync<LocalAttachmentStorage.AttachmentTicketInfo>(
                ticketKey, cancellationToken)
            .ConfigureAwait(false);
        if (info is null)
            return (false, null, null, null, null, 0, null, "上传票无效或已过期");
        if (info.UserId != userId)
            return (false, null, null, null, null, 0, null, "上传票与用户不匹配");
        if (!string.Equals(info.ObjectKey, objectKey, StringComparison.Ordinal))
            return (false, null, null, null, null, 0, null, "对象键与上传票不匹配");
        if (!objectKey.StartsWith($"attachments/{userId}/", StringComparison.Ordinal))
            return (false, null, null, null, null, 0, null, "对象键与用户不匹配");

        try
        {
            var meta = await _s3.GetObjectMetadataAsync(_options.S3Bucket, objectKey, cancellationToken)
                .ConfigureAwait(false);
            if (meta.ContentLength <= 0 || meta.ContentLength > MaxBytes)
            {
                await RestoreTicketAsync(ticketKey, info, cancellationToken).ConfigureAwait(false);
                return (false, null, null, null, null, 0, null, "附件大小超限");
            }

            var finalKey = objectKey;
            if (objectKey.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            {
                var ext = GuessExtension(info.ContentType, info.OriginalName);
                finalKey = $"attachments/{userId}/{info.AttachmentId}{ext}";
                await _s3.CopyObjectAsync(
                    _options.S3Bucket, objectKey,
                    _options.S3Bucket, finalKey,
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    await _s3.DeleteObjectAsync(_options.S3Bucket, objectKey, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "清理临时附件对象失败");
                }
            }

            return (true, string.Empty, finalKey, info.AttachmentId, info.ContentType, meta.ContentLength, info.OriginalName, null);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await RestoreTicketAsync(ticketKey, info, cancellationToken).ConfigureAwait(false);
            return (false, null, null, null, null, 0, null, "附件尚未上传完成");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "S3 附件确认失败");
            await RestoreTicketAsync(ticketKey, info, cancellationToken).ConfigureAwait(false);
            return (false, null, null, null, null, 0, null, "附件确认失败");
        }
    }

    public string? TryResolveLocalPhysicalPath(string objectKey) => null;

    public async Task<AttachmentReadResult?> OpenReadAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            return null;

        var key = NormalizeKey(objectKey);
        try
        {
            var response = await _s3.GetObjectAsync(
                    new GetObjectRequest
                    {
                        BucketName = _options.S3Bucket,
                        Key = key,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var contentType = string.IsNullOrWhiteSpace(response.Headers.ContentType)
                ? "application/octet-stream"
                : response.Headers.ContentType;
            var length = response.Headers.ContentLength;
            var fileName = Path.GetFileName(key);
            // 调用方负责 Dispose ResponseStream（经 AttachmentReadResult.Content）。
            return new AttachmentReadResult(
                response.ResponseStream,
                contentType,
                length,
                fileName);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<AttachmentSignedUrl?> CreateSignedDownloadUrlAsync(
        string objectKey,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            return null;

        var key = NormalizeKey(objectKey);
        var minutes = Math.Clamp(
            ttl?.TotalMinutes ?? _options.SignedDownloadMinutes,
            1,
            60);
        var expires = DateTimeOffset.UtcNow.AddMinutes(minutes);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.S3Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = expires.UtcDateTime,
        };
        var url = await _s3.GetPreSignedURLAsync(request).ConfigureAwait(false);
        return new AttachmentSignedUrl(url, expires);
    }

    public async Task DeleteAsync(string objectKeyOrUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKeyOrUrl))
            return;

        var key = NormalizeKey(objectKeyOrUrl);
        await _s3.DeleteObjectAsync(_options.S3Bucket, key, cancellationToken).ConfigureAwait(false);
    }

    public async Task TryDeleteAsync(string? objectKeyOrUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKeyOrUrl)) return;
        try
        {
            await DeleteAsync(objectKeyOrUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除 S3 附件失败 Key={Key}", objectKeyOrUrl);
        }
    }

    private string NormalizeKey(string objectKeyOrUrl)
    {
        var key = objectKeyOrUrl;
        var prefix = _options.PublicBaseUrl.TrimEnd('/') + "/";
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            key = key[prefix.Length..];
        return key;
    }

    private async Task RestoreTicketAsync(
        string ticketKey,
        LocalAttachmentStorage.AttachmentTicketInfo info,
        CancellationToken cancellationToken)
    {
        try
        {
            await _cache.SetStringPayloadAsync(
                ticketKey, info, TimeSpan.FromMinutes(Math.Clamp(_options.TicketMinutes, 1, 60)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "附件确认失败后写回上传票失败");
        }
    }

    private static string GuessExtension(string contentType, string? originalName)
    {
        if (!string.IsNullOrWhiteSpace(originalName))
        {
            var ext = Path.GetExtension(originalName);
            if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 16)
                return ext.ToLowerInvariant();
        }

        return contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "application/pdf" => ".pdf",
            "audio/mpeg" => ".mp3",
            "audio/ogg" => ".ogg",
            "video/mp4" => ".mp4",
            _ => ".bin",
        };
    }

    public void Dispose() => _s3.Dispose();
}
