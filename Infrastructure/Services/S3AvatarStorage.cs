using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Models.Token;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Infrastructure.Services;

/// <summary>S3/MinIO 预签名上传；确认时服务端拉取并重编码校验。</summary>
public sealed class S3AvatarStorage : IAvatarStorage, IAvatarConfirmRecovery, IAvatarUploadHeadersProvider, IAvatarPublicationStorage, IObjectStoreHealthProbe, IS3LifecycleHealthProbe, IDisposable
{
    private readonly AvatarStorageOptions _options;
    private readonly ICacheValueStore _cache;
    private readonly IAtomicCacheStore _atomicCache;
    private readonly ILogger<S3AvatarStorage> _logger;
    private readonly IAmazonS3 _s3;
    private readonly AvatarReencodeQueue _reencodeQueue;
    private const int MaxPixels = 2048;
    private const int OutputSize = 512;

    public S3AvatarStorage(
        IOptions<AvatarStorageOptions> options,
        ICacheValueStore cache,
        IAtomicCacheStore atomicCache,
        AvatarReencodeQueue reencodeQueue,
        ILogger<S3AvatarStorage> logger)
    {
        _options = options.Value;
        _cache = cache;
        _atomicCache = atomicCache;
        _reencodeQueue = reencodeQueue;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.S3Bucket))
            throw new InvalidOperationException("AvatarStorage S3 配置不完整");

        _s3 = S3ClientFactory.Create(
            _options.S3Region,
            _options.S3Endpoint,
            _options.S3ForcePathStyle);
    }

    public long MaxBytes => _options.MaxBytes;

    public bool IsAllowedContentType(string contentType) =>
        _options.AllowedContentTypes.Any(t =>
            string.Equals(t, contentType, StringComparison.OrdinalIgnoreCase));

    public async Task<(string ObjectKey, string Ticket, string UploadUrl, string PublicUrl, DateTimeOffset ExpiresAt)>
        CreateUploadTicketAsync(long userId, string contentType, long contentLength, CancellationToken cancellationToken = default)
    {
        if (!IsAllowedContentType(contentType))
            throw new ArgumentException("不支持的头像格式");
        if (contentLength <= 0 || contentLength > MaxBytes)
            throw new ArgumentException($"头像大小须在 1~{MaxBytes} 字节之间");

        var nonce = Guid.NewGuid().ToString("N");
        // Client-writable objects are isolated under pending/. The bucket
        // lifecycle rule can delete this prefix without ever touching a
        // confirmed avatar, including after a process crash between storage
        // confirmation and the UserDb transaction.
        var objectKey = $"avatars/{userId}/pending/{nonce}.bin";
        var ticket = TokenBufferEncoding.CreateHex(24);
        var expires = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TicketMinutes, 1, 60));

        await _cache.SetAsync(
            $"avatar:ticket:{ticket}",
            new LocalAvatarStorage.AvatarTicketInfo(userId, objectKey, contentType, contentLength, expires.ToUnixTimeMilliseconds()),
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
        request.Headers["x-amz-tagging"] = "chatapp-avatar-state=unconfirmed";
        var uploadUrl = await _s3.GetPreSignedURLAsync(request).ConfigureAwait(false);
        var publicUrl = $"{_options.PublicBaseUrl.TrimEnd('/')}/{objectKey}";
        return (objectKey, ticket, uploadUrl, publicUrl, expires);
    }

    public Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? Error)> StoreAsync(
        long userId, string ticket, Stream content, string contentType, CancellationToken cancellationToken = default)
        => Task.FromResult<(bool, string?, string?, string?)>(
            (false, null, null, "S3 模式请直传预签名 URL，再调用 confirm"));

    public async Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? Error)> ConfirmObjectAsync(
        long userId, string objectKey, string? ticket = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return (false, null, null, "确认头像须提供上传票");

        var ticketKey = $"avatar:ticket:{ticket}";
        // 先原子消费票，避免并发 confirm 双重 finalize / 孤儿对象
        var info = await _atomicCache.TryGetAndDeleteAsync<LocalAvatarStorage.AvatarTicketInfo>(
                ticketKey, cancellationToken)
            .ConfigureAwait(false);
        if (info is null)
            return (false, null, null, "上传票无效或已过期");
        if (info.UserId != userId)
            return (false, null, null, "上传票与用户不匹配");
        if (!string.Equals(info.ObjectKey, objectKey, StringComparison.Ordinal))
            return (false, null, null, "对象键与上传票不匹配");

        var (ok, finalKey, publicUrl, error) = await ValidateAndFinalizeAsync(userId, objectKey, cancellationToken)
            .ConfigureAwait(false);
        if (!ok)
        {
            // 校验失败时写回票，允许客户端重试（TTL 缩短）
            try
            {
                var remaining = DateTimeOffset.FromUnixTimeMilliseconds(info.ExpiresAtUnixMs)
                               - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    await _cache.SetAsync(
                            ticketKey, info, remaining, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "头像确认失败后写回上传票失败");
            }
        }

        return (ok, publicUrl, finalKey, error);
    }

    public async Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? Error)> RecoverConfirmedObjectAsync(
        long userId,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        // ValidateAndFinalizeAsync uses the deterministic final key and is
        // idempotent. It deliberately does not consume the upload ticket.
        var (ok, finalKey, publicUrl, error) = await ValidateAndFinalizeAsync(
                userId,
                objectKey,
                cancellationToken)
            .ConfigureAwait(false);
        return (ok, publicUrl, finalKey, error);
    }

    public async Task<bool> ObjectExistsAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(_options.S3Bucket, objectKey, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public string? GetPublicUrl(string objectKey) =>
        string.IsNullOrWhiteSpace(objectKey) ? null : $"{_options.PublicBaseUrl.TrimEnd('/')}/{objectKey}";

    public IReadOnlyDictionary<string, string> GetRequiredUploadHeaders(string contentType)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = contentType,
            ["x-amz-tagging"] = "chatapp-avatar-state=unconfirmed",
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

    public Task ValidateLifecycleAsync(CancellationToken cancellationToken = default) =>
        S3LifecycleConfigurationValidator.RequireAsync(
            _s3,
            _options.S3Bucket!,
            [S3LifecycleRequirement.Tag("chatapp-avatar-state", "unconfirmed")],
            cancellationToken);

    public async Task TryDeleteAsync(string? objectKeyOrUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKeyOrUrl)) return;
        try
        {
            var key = objectKeyOrUrl;
            var prefix = _options.PublicBaseUrl.TrimEnd('/') + "/";
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                key = key[prefix.Length..];
            await _s3.DeleteObjectAsync(_options.S3Bucket, key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除 S3 旧头像失败");
            throw;
        }
    }

    /// <summary>确认时：下载原图 → 校验像素 → 重编码写回最终 key。</summary>
    public async Task<(bool Ok, string? FinalKey, string? PublicUrl, string? Error)> ValidateAndFinalizeAsync(
        long userId, string objectKey, CancellationToken cancellationToken = default)
    {
        if (!objectKey.StartsWith($"avatars/{userId}/pending/", StringComparison.Ordinal))
            return (false, null, null, "对象键与用户不匹配");

        var rawPath = Path.Combine(Path.GetTempPath(), $"chatapp-avatar-{Guid.NewGuid():N}.raw");
        var encodedPath = Path.Combine(Path.GetTempPath(), $"chatapp-avatar-{Guid.NewGuid():N}.jpg");
        try
        {
            using (var obj = await _s3.GetObjectAsync(_options.S3Bucket, objectKey, cancellationToken)
                       .ConfigureAwait(false))
            {
                if (obj.ContentLength > MaxBytes)
                    return (false, null, null, "对象过大");

                await using var raw = new FileStream(
                    rawPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await obj.ResponseStream.CopyToAsync(raw, cancellationToken).ConfigureAwait(false);
                await raw.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var (finalKey, publicUrl, error) = await _reencodeQueue.RunAsync(async ct =>
            {
                await using var raw = new FileStream(
                    rawPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var image = await Image.LoadAsync(raw, ct).ConfigureAwait(false);
                if (image.Width > MaxPixels || image.Height > MaxPixels)
                    return (null as string, null as string, "图片像素尺寸超限");

                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(OutputSize, OutputSize),
                    Mode = ResizeMode.Crop,
                }));

                var key = BuildDeterministicFinalKey(userId, objectKey);
                await using (var encoded = new FileStream(
                                 encodedPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await image.SaveAsJpegAsync(encoded, new JpegEncoder { Quality = 85 }, ct)
                        .ConfigureAwait(false);
                    await encoded.FlushAsync(ct).ConfigureAwait(false);
                }

                await using var encodedInput = new FileStream(
                    encodedPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var put = new PutObjectRequest
                {
                    BucketName = _options.S3Bucket,
                    Key = key,
                    InputStream = encodedInput,
                    ContentType = "image/jpeg",
                };
                S3ClientFactory.ApplyServerSideEncryption(put, _options.S3SseMode, _options.S3KmsKeyId);
                put.TagSet =
                [
                    // The final key is still a candidate until the UserDb
                    // transaction references it. Lifecycle must be able to
                    // reclaim a process-crash orphan in this window.
                    new Tag { Key = "chatapp-avatar-state", Value = "unconfirmed" },
                ];
                await _s3.PutObjectAsync(put, ct).ConfigureAwait(false);

                return (key, GetPublicUrl(key), null as string);
            }, cancellationToken).ConfigureAwait(false);

            if (error is not null || finalKey is null)
                return (false, null, null, error ?? "头像确认失败");

            // Keep the uploaded candidate until the Server DB transaction
            // writes a durable avatar-delete tombstone. Deleting it here
            // would recreate the request-time cross-store orphan window.
            return (true, finalKey, publicUrl, null);
        }
        catch (UnknownImageFormatException)
        {
            return (false, null, null, "无法解码为有效图片");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "S3 头像确认失败");
            return (false, null, null, "头像确认失败");
        }
        finally
        {
            TryDeleteTempFile(rawPath);
            TryDeleteTempFile(encodedPath);
        }
    }

    /// <summary>
    /// Marks a candidate live after its AvatarUrl reference is durable. A
    /// repeated call is safe and is also used by candidate reconciliation.
    /// </summary>
    public Task PublishAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            return Task.CompletedTask;

        return _s3.PutObjectTaggingAsync(
            new PutObjectTaggingRequest
            {
                BucketName = _options.S3Bucket,
                Key = objectKey.TrimStart('/'),
                Tagging = new Tagging
                {
                    TagSet =
                    [
                        new Tag { Key = "chatapp-avatar-state", Value = "confirmed" },
                    ],
                },
            },
            cancellationToken);
    }

    private static string BuildDeterministicFinalKey(long userId, string pendingKey)
    {
        var slash = pendingKey.LastIndexOf('/');
        var fileName = slash >= 0 ? pendingKey[(slash + 1)..] : pendingKey;
        var nonce = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(nonce)
            || nonce.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_')))
            throw new InvalidOperationException("头像对象键 nonce 无效");
        return $"avatars/{userId}/confirmed/{nonce}.jpg";
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // The OS temp cleaner is the final fallback; do not turn a
            // successful durable object write into a failed request.
        }
    }

    public void Dispose() => _s3.Dispose();
}
