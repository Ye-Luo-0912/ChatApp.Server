using System.Security.Cryptography;
using System.Buffers;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// 开发用本地附件存储；票据存 Redis，可跨实例。不做重编码，原样落盘。
/// </summary>
public sealed class LocalAttachmentStorage(
    IOptions<AttachmentStorageOptions> options,
    ICacheValueStore cache,
    IAtomicCacheStore atomicCache,
    ILogger<LocalAttachmentStorage> logger) : IAttachmentStorage, IObjectStoreHealthProbe
{
    private readonly AttachmentStorageOptions _options = options.Value;

    public sealed record AttachmentTicketInfo(
        long UserId,
        string AttachmentId,
        string ObjectKey,
        string ContentType,
        long ContentLength,
        string? OriginalName,
        string? ClientAttachmentId,
        long ExpiresAtUnixMs);

    public long MaxBytes => _options.MaxBytes;

    public Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_options.LocalRootPath))
            throw new DirectoryNotFoundException(_options.LocalRootPath);
        return Task.CompletedTask;
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
        // Keep the final key stable from presign through confirm. The MIME type
        // lives in the attachment metadata row, not in the file name.
        var objectKey = $"{userId}/{attachmentId}";
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var expires = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TicketMinutes, 1, 60));
        // PublicUrl 仅作内部/遗留字段；聊天 API 使用 DownloadPath，不暴露永久静态 URL。
        var publicUrl = string.Empty;
        var ttl = expires - DateTimeOffset.UtcNow;

        await cache.SetAsync(
            TicketKey(ticket),
            new AttachmentTicketInfo(
                userId, attachmentId, objectKey, contentType, contentLength, originalName, clientAttachmentId,
                expires.ToUnixTimeMilliseconds()),
            ttl,
            cancellationToken).ConfigureAwait(false);

        var uploadUrl = $"/api/attachments/upload?ticket={Uri.EscapeDataString(ticket)}";
        return (attachmentId, objectKey, ticket, uploadUrl, publicUrl, expires);
    }

    public async Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? AttachmentId, long SizeBytes, string? Sha256Hex, string? Error)> StoreAsync(
        long userId, string ticket, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var ticketKey = TicketKey(ticket);
        var info = await atomicCache.TryGetAndDeleteAsync<AttachmentTicketInfo>(ticketKey, cancellationToken)
            .ConfigureAwait(false);
        if (info is null)
            return (false, null, null, null, 0, null, "上传票无效或已过期");
        if (info.UserId != userId)
            return (false, null, null, null, 0, null, "上传票与用户不匹配");
        if (!IsAllowedContentType(contentType)
            && !string.Equals(contentType, info.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            await RestoreTicketAsync(ticketKey, info, cancellationToken).ConfigureAwait(false);
            return (false, null, null, null, 0, null, "不支持的附件格式");
        }

        var root = EnsureDirectoryBoundary(_options.LocalRootPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, info.ObjectKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(root, fullPath))
            return (false, null, null, null, 0, null, "非法对象键");

        var tempPath = fullPath + ".uploading";
        long written = 0;
        string? shaHex = null;
        var oversized = false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            if (File.Exists(tempPath))
                File.Delete(tempPath);

            await using (var fs = new FileStream(
                             tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    while (true)
                    {
                        var read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                            break;

                        written += read;
                        if (written > MaxBytes)
                        {
                            oversized = true;
                            break;
                        }

                        hasher.AppendData(buffer, 0, read);
                        await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }

                    if (!oversized)
                    {
                        await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                        shaHex = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            if (oversized || written <= 0)
            {
                TryDeleteFile(tempPath);
                await RestoreTicketAsync(ticketKey, info, cancellationToken).ConfigureAwait(false);
                return (false, null, null, null, 0, null, "附件大小超限");
            }

            // 与票上 ContentLength 允许小幅偏差（客户端 Content-Length 可能不准）
            if (info.ContentLength > 0
                && Math.Abs(written - info.ContentLength) > Math.Max(1024, info.ContentLength / 10))
            {
                TryDeleteFile(tempPath);
                await RestoreTicketAsync(ticketKey, info, cancellationToken).ConfigureAwait(false);
                return (false, null, null, null, 0, null, "附件大小与预签不一致");
            }

            // 原子提升：目标已存在则失败（不覆盖）
            if (File.Exists(fullPath))
            {
                TryDeleteFile(tempPath);
                await RestoreTicketAsync(ticketKey, info, cancellationToken).ConfigureAwait(false);
                return (false, null, null, null, 0, null, "附件对象已存在");
            }

            File.Move(tempPath, fullPath);
        }
        catch (Exception ex)
        {
            TryDeleteFile(tempPath);
            logger.LogWarning(ex, "附件落盘失败 AttachmentId={Id}", info.AttachmentId);
            await RestoreTicketAsync(ticketKey, info, cancellationToken).ConfigureAwait(false);
            return (false, null, null, null, 0, null, "附件写入失败");
        }

        // Local：上传完成后把票写回短 TTL，供 confirm 消费（绑定 attachmentId/objectKey）。
        var confirmExpires = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TicketMinutes, 1, 60));
        var confirmInfo = info with
        {
            ContentLength = written,
            ExpiresAtUnixMs = confirmExpires.ToUnixTimeMilliseconds(),
        };
        await cache.SetAsync(
                ticketKey,
                confirmInfo,
                TimeSpan.FromMinutes(Math.Clamp(_options.TicketMinutes, 1, 60)),
                cancellationToken)
            .ConfigureAwait(false);

        return (true, string.Empty, info.ObjectKey, info.AttachmentId, written, shaHex, null);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* best effort */
        }
    }

    public async Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? AttachmentId, string? ContentType, long SizeBytes, string? OriginalName, string? Error)>
        ConfirmObjectAsync(
            long userId,
            string objectKey,
            string? ticket = null,
            string? attachmentId = null,
            CancellationToken cancellationToken = default)
    {
        if (!objectKey.StartsWith($"{userId}/", StringComparison.Ordinal))
            return (false, null, null, null, null, 0, null, "无效的附件对象键");

        AttachmentTicketInfo? info = null;
        if (!string.IsNullOrWhiteSpace(ticket))
        {
            var ticketKey = TicketKey(ticket);
            info = await atomicCache.TryGetAndDeleteAsync<AttachmentTicketInfo>(ticketKey, cancellationToken)
                .ConfigureAwait(false);
            if (info is null)
                return (false, null, null, null, null, 0, null, "上传票无效或已过期");
            if (info.UserId != userId)
                return (false, null, null, null, null, 0, null, "上传票与用户不匹配");
            if (!string.Equals(info.ObjectKey, objectKey, StringComparison.Ordinal))
            {
                await RestoreTicketAsync(ticketKey, info, cancellationToken).ConfigureAwait(false);
                return (false, null, null, null, null, 0, null, "对象键与上传票不匹配");
            }
        }

        if (!await ObjectExistsAsync(objectKey, cancellationToken).ConfigureAwait(false))
        {
            if (info is not null && !string.IsNullOrWhiteSpace(ticket))
                await RestoreTicketAsync(TicketKey(ticket!), info, cancellationToken).ConfigureAwait(false);
            return (false, null, null, null, null, 0, null, "附件尚未上传完成");
        }

        var id = info?.AttachmentId ?? attachmentId;
        if (string.IsNullOrWhiteSpace(id))
            return (false, null, null, null, null, 0, null, "缺少 attachmentId");

        long size = info?.ContentLength ?? 0;
        if (size <= 0)
        {
            try
            {
                var root = EnsureDirectoryBoundary(_options.LocalRootPath);
                var fullPath = Path.GetFullPath(Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar)));
                if (IsUnderRoot(root, fullPath))
                    size = new FileInfo(fullPath).Length;
            }
            catch { /* best effort */ }
        }

        return (true, string.Empty, objectKey, id, info?.ContentType, size, info?.OriginalName, null);
    }

    public string? TryResolveLocalPhysicalPath(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            return null;

        var root = EnsureDirectoryBoundary(_options.LocalRootPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(root, fullPath) || !File.Exists(fullPath))
            return null;

        return fullPath;
    }

    public Task<AttachmentReadResult?> OpenReadAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        var fullPath = TryResolveLocalPhysicalPath(objectKey);
        if (fullPath is null)
            return Task.FromResult<AttachmentReadResult?>(null);

        var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        // MIME comes from the realtime attachment metadata row. The object key
        // intentionally has no extension; use a safe fallback for direct probes.
        const string contentType = "application/octet-stream";
        return Task.FromResult<AttachmentReadResult?>(
            new AttachmentReadResult(stream, contentType, stream.Length, Path.GetFileName(fullPath)));
    }

    public Task<AttachmentSignedUrl?> CreateSignedDownloadUrlAsync(
        string objectKey,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<AttachmentSignedUrl?>(null);

    public Task DeleteAsync(string objectKeyOrUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKeyOrUrl))
            return Task.CompletedTask;

        var key = NormalizeKey(objectKeyOrUrl);
        var root = EnsureDirectoryBoundary(_options.LocalRootPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(root, fullPath))
            throw new InvalidOperationException("非法附件对象键");
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public async Task TryDeleteAsync(string? objectKeyOrUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKeyOrUrl))
            return;

        try
        {
            await DeleteAsync(objectKeyOrUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "删除附件失败 Key={Key}", objectKeyOrUrl);
        }
    }

    private Task<bool> ObjectExistsAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var root = EnsureDirectoryBoundary(_options.LocalRootPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(root, fullPath))
            return Task.FromResult(false);
        return Task.FromResult(File.Exists(fullPath));
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
        string ticketKey, AttachmentTicketInfo info, CancellationToken cancellationToken)
    {
        // P0 正确性：恢复时使用原始绝对截止时间的剩余 TTL，不重置为完整 TicketMinutes。
        // 多次失败恢复不会延长票据寿命超过原始截止时间；已过期则不再恢复。
        var remaining = DateTimeOffset.FromUnixTimeMilliseconds(info.ExpiresAtUnixMs) - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            logger.LogWarning("附件上传票已过期，不再恢复，ExpiresAtUnixMs={ExpiresAtUnixMs}", info.ExpiresAtUnixMs);
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
            logger.LogWarning(ex, "附件上传失败后写回上传票失败");
        }
    }

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

    private static string TicketKey(string ticket) => $"attachment:ticket:{ticket}";
}
