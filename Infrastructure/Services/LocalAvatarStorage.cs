using System.Buffers;
using System.Security.Cryptography;
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

/// <summary>
/// 开发用本地头像存储；票据存 Redis，可跨实例。
/// </summary>
public sealed class LocalAvatarStorage(
    IOptions<AvatarStorageOptions> options,
    ICacheValueStore cache,
    IAtomicCacheStore atomicCache,
    AvatarReencodeQueue reencodeQueue,
    ILogger<LocalAvatarStorage> logger) : IAvatarStorage, IAvatarConfirmRecovery, IAvatarPublicationStorage
{
    private readonly AvatarStorageOptions _options = options.Value;
    private const int MaxPixels = 2048;
    private const int OutputSize = 512;

    public sealed record AvatarTicketInfo(long UserId, string ObjectKey, string ContentType, long ContentLength, long ExpiresAtUnixMs);

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

        // Keep the same candidate/final lifecycle as the S3 provider.  The
        // local provider does not expose a public URL for the pending object;
        // it is only a crash-cleanable staging path.
        var objectKey = $"{userId}/pending/{Guid.NewGuid():N}.bin";
        var ticket = TokenBufferEncoding.CreateHex(24);
        var expires = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TicketMinutes, 1, 60));
        var publicUrl = $"{_options.PublicBaseUrl.TrimEnd('/')}/{objectKey}";
        var ttl = expires - DateTimeOffset.UtcNow;

        await cache.SetAsync(
            TicketKey(ticket),
            new AvatarTicketInfo(userId, objectKey, contentType, contentLength, expires.ToUnixTimeMilliseconds()),
            ttl,
            cancellationToken).ConfigureAwait(false);

        var uploadUrl = $"/api/users/me/avatar/upload?ticket={Uri.EscapeDataString(ticket)}";
        return (objectKey, ticket, uploadUrl, publicUrl, expires);
    }

    public async Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? Error)> StoreAsync(
        long userId, string ticket, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var ticketKey = TicketKey(ticket);
        var info = await atomicCache.TryGetAndDeleteAsync<AvatarTicketInfo>(ticketKey, cancellationToken)
            .ConfigureAwait(false);
        if (info is null)
            return (false, null, null, "上传票无效或已过期");
        if (info.UserId != userId)
            return (false, null, null, "上传票与用户不匹配");
        if (!IsAllowedContentType(contentType))
        {
            await RestoreTicketAsync(ticketKey, info, cancellationToken).ConfigureAwait(false);
            return (false, null, null, "不支持的头像格式");
        }

        var root = EnsureDirectoryBoundary(_options.LocalRootPath);
        var pendingPath = Path.GetFullPath(
            Path.Combine(root, info.ObjectKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(root, pendingPath))
            return (false, null, null, "非法对象键");

        var nonce = Path.GetFileNameWithoutExtension(pendingPath);
        var finalObjectKey = $"{userId}/confirmed/{nonce}.jpg";
        var finalPath = Path.GetFullPath(
            Path.Combine(root, finalObjectKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(root, finalPath))
            return (false, null, null, "非法对象键");

        var pendingUploadPath = pendingPath + ".uploading";
        // Keep both temporary files under pending/. A crash must not leave an
        // untracked .uploading file below confirmed/, which is intentionally
        // never scanned by the cleanup worker.
        var encodedUploadPath = pendingPath + ".encoded.uploading";
        try
        {
            // Persist the raw candidate first.  A process exit during the
            // request can therefore leave only a pending/*.bin candidate,
            // which the bounded cleanup path is allowed to remove.
            Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
            await using (var pending = new FileStream(
                             pendingUploadPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var bytesWritten = await CopyToFileAsync(
                        content, pending, MaxBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (bytesWritten <= 0 || bytesWritten > MaxBytes)
                {
                    TryDeleteFile(pendingUploadPath);
                    await RestoreTicketAsync(ticketKey, info, cancellationToken)
                        .ConfigureAwait(false);
                    return (false, null, null, "头像大小超限");
                }

                await pending.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await reencodeQueue.RunAsync(
                    async ct =>
                    {
                        await ReencodeToFileAsync(
                                pendingUploadPath, encodedUploadPath, ct)
                            .ConfigureAwait(false);
                        return true;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnknownImageFormatException)
        {
            TryDeleteFile(pendingUploadPath);
            TryDeleteFile(encodedUploadPath);
            TryDeleteFile(pendingPath);
            await RestoreTicketAsync(ticketKey, info, cancellationToken).ConfigureAwait(false);
            return (false, null, null, "无法解码为有效图片");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "头像解码失败");
            TryDeleteFile(pendingUploadPath);
            TryDeleteFile(encodedUploadPath);
            TryDeleteFile(pendingPath);
            await RestoreTicketAsync(ticketKey, info, cancellationToken).ConfigureAwait(false);
            return (false, null, null, "图片处理失败");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            // A same-volume move is the local equivalent of an idempotent
            // promotion. Never overwrite a different confirmed avatar.
            File.Move(encodedUploadPath, finalPath, overwrite: false);
            TryDeleteFile(pendingUploadPath);
            TryDeleteFile(pendingPath);
        }
        catch (Exception ex)
        {
            TryDeleteFile(encodedUploadPath);
            TryDeleteFile(pendingUploadPath);
            TryDeleteFile(pendingPath);
            logger.LogWarning(ex, "头像候选提升失败 UserId={UserId}", userId);
            await RestoreTicketAsync(ticketKey, info, cancellationToken).ConfigureAwait(false);
            return (false, null, null, "图片处理失败");
        }

        var publicUrl = $"{_options.PublicBaseUrl.TrimEnd('/')}/{finalObjectKey}";
        return (true, publicUrl, finalObjectKey, null);
    }

    public async Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? Error)> ConfirmObjectAsync(
        long userId, string objectKey, string? ticket = null, CancellationToken cancellationToken = default)
    {
        if (!IsOwnedObjectKey(userId, objectKey))
            return (false, null, null, "无效的头像对象键");
        if (!objectKey.StartsWith($"{userId}/confirmed/", StringComparison.Ordinal))
            return (false, null, null, "本地头像必须先通过上传接口完成处理");
        if (!await ObjectExistsAsync(objectKey, cancellationToken).ConfigureAwait(false))
            return (false, null, null, "头像尚未上传完成");
        return (true, GetPublicUrl(objectKey), objectKey, null);
    }

    public async Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? Error)> RecoverConfirmedObjectAsync(
        long userId,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        var result = await ConfirmObjectAsync(userId, objectKey, ticket: null, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    public Task<bool> ObjectExistsAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var root = EnsureDirectoryBoundary(_options.LocalRootPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(root, fullPath))
            return Task.FromResult(false);
        return Task.FromResult(File.Exists(fullPath));
    }

    public string? GetPublicUrl(string objectKey) =>
        string.IsNullOrWhiteSpace(objectKey) ? null : $"{_options.PublicBaseUrl.TrimEnd('/')}/{objectKey}";

    public Task TryDeleteAsync(string? objectKeyOrUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKeyOrUrl))
            return Task.CompletedTask;

        try
        {
            var key = objectKeyOrUrl;
            var prefix = _options.PublicBaseUrl.TrimEnd('/') + "/";
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                key = key[prefix.Length..];

            var root = EnsureDirectoryBoundary(_options.LocalRootPath);
            var fullPath = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
            if (IsUnderRoot(root, fullPath) && File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "删除旧头像失败");
            throw;
        }

        return Task.CompletedTask;
    }

    // Local storage has no object tags. The durable avatar candidate row and
    // the UserDb AvatarVersion CAS provide the same publication fence.
    public Task PublishAsync(string objectKey, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    private async Task RestoreTicketAsync(
        string ticketKey, AvatarTicketInfo info, CancellationToken cancellationToken)
    {
        // P0 正确性：恢复时使用原始绝对截止时间的剩余 TTL，不重置为完整 TicketMinutes。
        // 多次失败恢复不会延长票据寿命超过原始截止时间；已过期则不再恢复。
        var remaining = DateTimeOffset.FromUnixTimeMilliseconds(info.ExpiresAtUnixMs) - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            logger.LogWarning("头像上传票已过期，不再恢复，ExpiresAtUnixMs={ExpiresAtUnixMs}", info.ExpiresAtUnixMs);
            return;
        }

        try
        {
            await cache.SetAsync(
                    ticketKey,
                    info,
                    remaining,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "头像上传失败后写回上传票失败");
        }
    }

    private static async Task ReencodeToFileAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            inputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var image = await Image.LoadAsync(input, cancellationToken).ConfigureAwait(false);
        if (image.Width > MaxPixels || image.Height > MaxPixels)
            throw new InvalidOperationException("图片像素尺寸超限");

        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(OutputSize, OutputSize),
            Mode = ResizeMode.Crop,
        }));

        await using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await image.SaveAsJpegAsync(
                output,
                new JpegEncoder { Quality = 85 },
                cancellationToken)
            .ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> CopyToFileAsync(
        Stream source,
        FileStream destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(
                        buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    return total;

                total += read;
                if (total > maxBytes)
                    return total;

                await destination.WriteAsync(
                        buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    /// <summary>确保根路径以分隔符结尾，并用相对路径检测逃逸。</summary>
    private static string EnsureDirectoryBoundary(string rootPath)
    {
        var full = Path.GetFullPath(rootPath);
        return full.EndsWith(Path.DirectorySeparatorChar)
            ? full
            : full + Path.DirectorySeparatorChar;
    }

    private static bool IsUnderRoot(string rootWithSep, string fullPath)
    {
        var relative = Path.GetRelativePath(rootWithSep, fullPath);
        return relative != ".."
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    private static string TicketKey(string ticket) => $"avatar:ticket:{ticket}";

    private static bool IsOwnedObjectKey(long userId, string objectKey) =>
        objectKey.StartsWith($"{userId}/confirmed/", StringComparison.Ordinal)
        || objectKey.StartsWith($"{userId}/pending/", StringComparison.Ordinal)
        // Keep already-published development objects readable during the
        // path migration; new writes never use this legacy layout.
        || objectKey.StartsWith($"{userId}/", StringComparison.Ordinal)
           && objectKey.IndexOf('/', objectKey.IndexOf('/') + 1) < 0;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // The lifecycle worker is the durable fallback for local
            // candidates; cleanup failure must not hide the upload result.
        }
    }
}
