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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Infrastructure.Services;

/// <summary>S3/MinIO 预签名上传；确认时服务端拉取并重编码校验。</summary>
public sealed class S3AvatarStorage : IAvatarStorage, IDisposable
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

        if (string.IsNullOrWhiteSpace(_options.S3Bucket)
            || string.IsNullOrWhiteSpace(_options.S3AccessKey)
            || string.IsNullOrWhiteSpace(_options.S3SecretKey))
            throw new InvalidOperationException("AvatarStorage S3 配置不完整");

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

    public async Task<(string ObjectKey, string Ticket, string UploadUrl, string PublicUrl, DateTimeOffset ExpiresAt)>
        CreateUploadTicketAsync(long userId, string contentType, long contentLength, CancellationToken cancellationToken = default)
    {
        if (!IsAllowedContentType(contentType))
            throw new ArgumentException("不支持的头像格式");
        if (contentLength <= 0 || contentLength > MaxBytes)
            throw new ArgumentException($"头像大小须在 1~{MaxBytes} 字节之间");

        var nonce = Guid.NewGuid().ToString("N");
        var objectKey = $"avatars/{userId}/{nonce}.bin";
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var expires = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TicketMinutes, 1, 60));

        await _cache.SetAsync(
            $"avatar:ticket:{ticket}",
            new LocalAvatarStorage.AvatarTicketInfo(userId, objectKey, contentType, contentLength),
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
        var publicUrl = $"{_options.PublicBaseUrl.TrimEnd('/')}/{objectKey}";
        return (objectKey, ticket, uploadUrl, publicUrl, expires);
    }

    public Task<(bool Ok, string? PublicUrl, string? Error)> StoreAsync(
        long userId, string ticket, Stream content, string contentType, CancellationToken cancellationToken = default)
        => Task.FromResult<(bool, string?, string?)>(
            (false, null, "S3 模式请直传预签名 URL，再调用 confirm"));

    public async Task<(bool Ok, string? PublicUrl, string? Error)> ConfirmObjectAsync(
        long userId, string objectKey, string? ticket = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return (false, null, "确认头像须提供上传票");

        var ticketKey = $"avatar:ticket:{ticket}";
        // 先原子消费票，避免并发 confirm 双重 finalize / 孤儿对象
        var info = await _atomicCache.TryGetAndDeleteAsync<LocalAvatarStorage.AvatarTicketInfo>(
                ticketKey, cancellationToken)
            .ConfigureAwait(false);
        if (info is null)
            return (false, null, "上传票无效或已过期");
        if (info.UserId != userId)
            return (false, null, "上传票与用户不匹配");
        if (!string.Equals(info.ObjectKey, objectKey, StringComparison.Ordinal))
            return (false, null, "对象键与上传票不匹配");

        var (ok, _, publicUrl, error) = await ValidateAndFinalizeAsync(userId, objectKey, cancellationToken)
            .ConfigureAwait(false);
        if (!ok)
        {
            // 校验失败时写回票，允许客户端重试（TTL 缩短）
            try
            {
                await _cache.SetAsync(
                    ticketKey, info, TimeSpan.FromMinutes(Math.Clamp(_options.TicketMinutes, 1, 60)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "头像确认失败后写回上传票失败");
            }
        }

        return (ok, publicUrl, error);
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
        }
    }

    /// <summary>确认时：下载原图 → 校验像素 → 重编码写回最终 key。</summary>
    public async Task<(bool Ok, string? FinalKey, string? PublicUrl, string? Error)> ValidateAndFinalizeAsync(
        long userId, string objectKey, CancellationToken cancellationToken = default)
    {
        if (!objectKey.StartsWith($"avatars/{userId}/", StringComparison.Ordinal))
            return (false, null, null, "对象键与用户不匹配");

        try
        {
            using var obj = await _s3.GetObjectAsync(_options.S3Bucket, objectKey, cancellationToken)
                .ConfigureAwait(false);
            if (obj.ContentLength > MaxBytes)
                return (false, null, null, "对象过大");

            await using var raw = new MemoryStream();
            await obj.ResponseStream.CopyToAsync(raw, cancellationToken).ConfigureAwait(false);
            raw.Position = 0;

            var (finalKey, publicUrl, error) = await _reencodeQueue.RunAsync(async ct =>
            {
                raw.Position = 0;
                using var image = await Image.LoadAsync(raw, ct).ConfigureAwait(false);
                if (image.Width > MaxPixels || image.Height > MaxPixels)
                    return (null as string, null as string, "图片像素尺寸超限");

                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(OutputSize, OutputSize),
                    Mode = ResizeMode.Crop,
                }));

                var key = $"avatars/{userId}/{Guid.NewGuid():N}.jpg";
                await using var encoded = new MemoryStream();
                await image.SaveAsJpegAsync(encoded, new JpegEncoder { Quality = 85 }, ct)
                    .ConfigureAwait(false);
                encoded.Position = 0;

                await _s3.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = _options.S3Bucket,
                    Key = key,
                    InputStream = encoded,
                    ContentType = "image/jpeg",
                }, ct).ConfigureAwait(false);

                return (key, GetPublicUrl(key), null as string);
            }, cancellationToken).ConfigureAwait(false);

            if (error is not null || finalKey is null)
                return (false, null, null, error ?? "头像确认失败");

            try
            {
                await _s3.DeleteObjectAsync(_options.S3Bucket, objectKey, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "清理临时头像对象失败");
            }

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
    }

    public void Dispose() => _s3.Dispose();
}
