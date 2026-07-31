using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>S3/MinIO 预签名附件上传；确认时校验对象并复用稳定最终键。</summary>
public sealed class S3AttachmentStorage : IAttachmentStorage, IAttachmentUploadHeadersProvider, IAttachmentScanStateMarker, IObjectStoreHealthProbe, IDisposable
{
    private readonly AttachmentStorageOptions _options;
    private readonly ICacheValueStore _cache;
    private readonly IAtomicCacheStore _atomicCache;
    private readonly AttachmentBlobDeleteEnqueuer _blobDeletes;
    private readonly ILogger<S3AttachmentStorage> _logger;
    private readonly IAmazonS3 _s3;

    public S3AttachmentStorage(
        IOptions<AttachmentStorageOptions> options,
        ICacheValueStore cache,
        IAtomicCacheStore atomicCache,
        AttachmentBlobDeleteEnqueuer blobDeletes,
        ILogger<S3AttachmentStorage> logger)
    {
        _options = options.Value;
        _cache = cache;
        _atomicCache = atomicCache;
        _blobDeletes = blobDeletes;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.S3Bucket))
            throw new InvalidOperationException("AttachmentStorage S3 配置不完整");

        _s3 = S3ClientFactory.Create(
            _options.S3Region,
            _options.S3Endpoint,
            _options.S3ForcePathStyle);
    }

    public long MaxBytes => _options.MaxBytes;

    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(
                    _options.S3Bucket,
                    "__chatapp_healthcheck_nonexistent__",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // A 404 proves that the bucket and credentials are reachable.
        }
    }

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
        // The final key is allocated once. MIME is carried by S3 Content-Type and
        // the realtime metadata row; never copy a large object merely to add an extension.
        var objectKey = $"attachments/{userId}/{attachmentId}";
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var expires = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TicketMinutes, 1, 60));

        await _cache.SetAsync(
            $"attachment:ticket:{ticket}",
            new LocalAttachmentStorage.AttachmentTicketInfo(
                userId, attachmentId, objectKey, contentType, contentLength, originalName, clientAttachmentId,
                expires.ToUnixTimeMilliseconds()),
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
        S3ClientFactory.ApplyServerSideEncryption(request, _options.S3SseMode, _options.S3KmsKeyId);
        request.Headers["x-amz-tagging"] = "chatapp-scan-state=unconfirmed";
        var uploadUrl = await _s3.GetPreSignedURLAsync(request).ConfigureAwait(false);
        // 不再返回永久 PublicUrl；聊天侧走鉴权下载。
        return (attachmentId, objectKey, ticket, uploadUrl, string.Empty, expires);
    }

    public IReadOnlyDictionary<string, string> GetRequiredUploadHeaders(string contentType)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = contentType,
            ["x-amz-tagging"] = "chatapp-scan-state=unconfirmed",
        };

        switch (S3ClientFactory.NormalizeMode(_options.S3SseMode))
        {
            case "SSE-S3":
                headers["x-amz-server-side-encryption"] = "AES256";
                break;
            case "SSE-KMS":
                headers["x-amz-server-side-encryption"] = "aws:kms";
                if (!string.IsNullOrWhiteSpace(_options.S3KmsKeyId))
                    headers["x-amz-server-side-encryption-aws-kms-key-id"] = _options.S3KmsKeyId;
                break;
        }

        return headers;
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
        var info = await _atomicCache.TryGetAndDeleteAsync<LocalAttachmentStorage.AttachmentTicketInfo>(
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
                try
                {
                    // A client can replace the body behind a PUT URL. Do not restore
                    // the ticket: the object is now a durable deletion concern, not a
                    // retryable upload.
                    await _blobDeletes.EnqueueAsync(
                            [(objectKey, info.AttachmentId)], userId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "超限附件对象删除任务入队失败 AttachmentId={AttachmentId} Key={Key}",
                        info.AttachmentId,
                        objectKey);
                }

                return (false, null, null, null, null, 0, null, "附件大小超限");
            }

            var contentType = string.IsNullOrWhiteSpace(meta.Headers.ContentType)
                ? info.ContentType
                : meta.Headers.ContentType;
            try
            {
                await MarkScanStateAsync(objectKey, "quarantine", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "附件对象无法进入 quarantine 状态，写入删除任务 AttachmentId={AttachmentId} Key={Key}",
                    info.AttachmentId,
                    objectKey);
                try
                {
                    await _blobDeletes.EnqueueAsync(
                            [(objectKey, info.AttachmentId)], userId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception enqueueEx)
                {
                    _logger.LogError(
                        enqueueEx,
                        "quarantine 失败后的附件删除任务入队失败 AttachmentId={AttachmentId} Key={Key}",
                        info.AttachmentId,
                        objectKey);
                }

                return (false, null, null, null, null, 0, null, "附件无法进入隔离扫描状态");
            }
            return (true, string.Empty, objectKey, info.AttachmentId, contentType, meta.ContentLength, info.OriginalName, null);
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

    public Task MarkScanStateAsync(
        string objectKey,
        string state,
        CancellationToken cancellationToken = default)
        => _s3.PutObjectTaggingAsync(
                new PutObjectTaggingRequest
                {
                    BucketName = _options.S3Bucket,
                    Key = NormalizeKey(objectKey),
                    Tagging = new Tagging
                    {
                        TagSet =
                        [
                            new Tag { Key = "chatapp-scan-state", Value = state },
                        ],
                    },
                },
                cancellationToken);

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
        // P0 正确性：恢复时使用原始绝对截止时间的剩余 TTL，不重置为完整 TicketMinutes。
        var remaining = DateTimeOffset.FromUnixTimeMilliseconds(info.ExpiresAtUnixMs) - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _logger.LogWarning("附件上传票已过期，不再恢复，ExpiresAtUnixMs={ExpiresAtUnixMs}", info.ExpiresAtUnixMs);
            return;
        }

        try
        {
            await _cache.SetAsync(
                ticketKey, info, remaining,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "附件确认失败后写回上传票失败");
        }
    }

    public void Dispose() => _s3.Dispose();
}
