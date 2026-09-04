using System.Security.Cryptography;
using System.Buffers;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Models.Token;
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
        var ticket = TokenBufferEncoding.CreateHex(24);
        var expires = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TicketMinutes, 1, 60));
        // PublicUrl 仅作内部/遗留字段；聊天 API 使用 DownloadPath，不暴露永久静态 URL。
        var publicUrl = string.Empty;
        var ttl = expires - DateTimeOffset.UtcNow;

        await cache.SetAsync(
            AttachmentUploadTicketKeys.Create(ticket),
            new AttachmentUploadTicket(
                userId, attachmentId, objectKey, contentType, contentLength, originalName, clientAttachmentId,
                expires.ToUnixTimeMilliseconds()),
            ttl,
            cancellationToken).ConfigureAwait(false);

        var uploadUrl = $"/api/attachments/upload?ticket={Uri.EscapeDataString(ticket)}";
        return (attachmentId, objectKey, ticket, uploadUrl, publicUrl, expires);
    }

    public Task CancelUploadTicketAsync(
        string ticket,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return Task.CompletedTask;
        // 普通票与去重票都可能存在：两键皆清（幂等）。
        return Task.WhenAll(
            cache.RemoveAsync(AttachmentUploadTicketKeys.Create(ticket), cancellationToken),
            cache.RemoveAsync(AttachmentUploadTicketKeys.CreateDedup(ticket), cancellationToken));
    }

    public async Task<(string AttachmentId, string ObjectKey, string Ticket, string UploadUrl, string PublicUrl, DateTimeOffset ExpiresAt)>
        CreateDedupTicketAsync(
            long userId,
            string sourceObjectKey,
            string contentType,
            long contentLength,
            string sha256,
            string? originalName = null,
            string? clientAttachmentId = null,
            CancellationToken cancellationToken = default)
    {
        if (!IsAllowedContentType(contentType))
            throw new ArgumentException("不支持的附件格式");
        if (contentLength <= 0 || contentLength > MaxBytes)
            throw new ArgumentException($"附件大小须在 1~{MaxBytes} 字节之间");
        if (string.IsNullOrWhiteSpace(sourceObjectKey))
            throw new ArgumentException("缺少秒传源对象键");
        if (!IsContentAddress(sha256))
            throw new ArgumentException("秒传 SHA-256 格式无效");

        var attachmentId = Guid.NewGuid().ToString("N");
        var objectKey = $"{userId}/{attachmentId}";
        var ticket = TokenBufferEncoding.CreateHex(24);
        var expires = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TicketMinutes, 1, 60));
        var ttl = expires - DateTimeOffset.UtcNow;

        await cache.SetAsync(
            AttachmentUploadTicketKeys.CreateDedup(ticket),
            new AttachmentDedupUploadTicket(
                userId, attachmentId, objectKey, sourceObjectKey, sha256,
                contentType, contentLength, originalName, clientAttachmentId,
                expires.ToUnixTimeMilliseconds()),
            ttl,
            cancellationToken).ConfigureAwait(false);

        // 去重票无 PUT：UploadUrl 恒为空，客户端直接确认。
        return (attachmentId, objectKey, ticket, string.Empty, string.Empty, expires);
    }

    public async Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? AttachmentId, long SizeBytes, string? Sha256Hex, string? Error)> StoreAsync(
        long userId, string ticket, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var ticketKey = AttachmentUploadTicketKeys.Create(ticket);
        var info = await atomicCache.TryGetAndDeleteAsync<AttachmentUploadTicket>(ticketKey, cancellationToken)
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
                        shaHex = GetHashHex(hasher)
                                 ?? throw new CryptographicException("附件哈希计算失败");
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

    public async Task<(bool Ok, bool Completed, long Received, string? AttachmentId, string? Sha256Hex, string? Error)>
        AppendUploadChunkAsync(
            long userId, string ticket, long offset, Stream chunk, string contentType,
            CancellationToken cancellationToken = default)
    {
        var ticketKey = AttachmentUploadTicketKeys.Create(ticket);

        // 续传期间票只 peek 不消费：追加失败后票天然保持有效，无需 RestoreTicketAsync 补偿。
        var info = await cache.GetAsync<AttachmentUploadTicket>(ticketKey, cancellationToken).ConfigureAwait(false);
        if (info is null)
            return (false, false, 0, null, null, "上传票无效或已过期");
        // GetAsync 与 TTL 删除间存在窄窗口：显式校验绝对截止时间，过期即拒并清理 partial。
        if (info.ExpiresAtUnixMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            TryDeleteTicketPartial(info);
            return (false, false, 0, info.AttachmentId, null, "上传票无效或已过期");
        }
        if (info.UserId != userId)
        {
            TryDeleteTicketPartial(info);
            return (false, false, 0, info.AttachmentId, null, "上传票与用户不匹配");
        }
        if (!IsAllowedContentType(contentType)
            && !string.Equals(contentType, info.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            return (false, false, 0, info.AttachmentId, null, "不支持的附件格式");
        }

        if (!TryResolveTicketPaths(info, out var fullPath, out var tempPath, out var pathError))
            return (false, false, 0, info.AttachmentId, null, pathError);

        // 定稿后的幂等重入：对象已在最终路径，避免对已完成上传重复 finalize。
        if (File.Exists(fullPath))
        {
            var done = new FileInfo(fullPath).Length;
            return offset == done
                ? (true, true, done, info.AttachmentId, null, null)
                : (false, false, done, info.AttachmentId, null, "附件对象已存在");
        }

        // 服务端权威：offset 必须等于当前 partial 长度，错位即拒绝并回传权威值。
        var received = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
        if (offset != received)
            return (false, false, received, info.AttachmentId, null, "续传偏移与服务端不一致");

        var oversized = false;
        var overDeclared = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await using (var fs = new FileStream(
                             tempPath,
                             offset == 0 ? FileMode.Create : FileMode.Open,
                             FileAccess.Write, FileShare.None,
                             bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (offset > 0)
                    fs.Seek(offset, SeekOrigin.Begin);

                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    while (true)
                    {
                        var read = await chunk.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                            break;

                        received += read;
                        if (received > MaxBytes)
                        {
                            oversized = true;
                            break;
                        }
                        if (info.ContentLength > 0 && received > info.ContentLength)
                        {
                            overDeclared = true;
                            break;
                        }

                        await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }

                    if (!oversized && !overDeclared)
                        await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch (Exception ex)
        {
            // 传输/写盘中断：partial 与票均保留，客户端按服务端权威 received 续传。
            logger.LogWarning(
                ex, "附件分块追加中断 AttachmentId={Id} Received={Received}", info.AttachmentId, received);
            return (false, false, received, info.AttachmentId, null, "附件写入失败");
        }

        if (oversized)
        {
            // 累计超上限：partial 不可救，删除防驻留；票未消费无需恢复。
            TryDeleteFile(tempPath);
            return (false, false, 0, info.AttachmentId, null, "附件大小超限");
        }
        if (overDeclared)
        {
            // 超过票声明长度：永远无法满足 == 条件定稿，删除 partial 要求客户端重传。
            TryDeleteFile(tempPath);
            return (false, false, 0, info.AttachmentId, null, "附件大小与预签不一致");
        }

        if (info.ContentLength <= 0 || received != info.ContentLength)
            return (true, false, received, info.AttachmentId, null, null);

        // 定稿：原子消费票，保证并发/重复 finalize 至多一个赢家。
        var consumed = await atomicCache.TryGetAndDeleteAsync<AttachmentUploadTicket>(ticketKey, cancellationToken)
            .ConfigureAwait(false);
        if (consumed is null)
        {
            // 票已消失（过期或被并发 finalize）：对象已落盘视为完成，否则清理 partial。
            if (File.Exists(fullPath))
                return (true, true, received, info.AttachmentId, null, null);
            TryDeleteFile(tempPath);
            return (false, false, received, info.AttachmentId, null, "上传票无效或已过期");
        }
        if (consumed.UserId != userId
            || !string.Equals(consumed.ObjectKey, info.ObjectKey, StringComparison.Ordinal))
        {
            await RestoreTicketAsync(ticketKey, consumed, cancellationToken).ConfigureAwait(false);
            return (false, false, received, info.AttachmentId, null, "上传票与用户不匹配");
        }

        string? shaHex;
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                // 对 temp 文件流式计算 SHA-256，分块哈希语义与整包路径一致。
                await using (var fs = new FileStream(
                                 tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                 bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    int read;
                    while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                               .ConfigureAwait(false)) > 0)
                    {
                        hasher.AppendData(buffer, 0, read);
                    }
                }

                shaHex = GetHashHex(hasher) ?? throw new CryptographicException("附件哈希计算失败");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // 原子提升：目标已存在则失败（不覆盖），与整包路径一致。
            if (File.Exists(fullPath))
            {
                TryDeleteFile(tempPath);
                await RestoreTicketAsync(ticketKey, consumed, cancellationToken).ConfigureAwait(false);
                return (false, false, received, info.AttachmentId, null, "附件对象已存在");
            }

            File.Move(tempPath, fullPath);
        }
        catch (Exception ex)
        {
            TryDeleteFile(tempPath);
            logger.LogWarning(ex, "附件续传定稿失败 AttachmentId={Id}", info.AttachmentId);
            await RestoreTicketAsync(ticketKey, consumed, cancellationToken).ConfigureAwait(false);
            return (false, false, received, info.AttachmentId, null, "附件写入失败");
        }

        // 与整包路径一致：票写回短 TTL 供 confirm 消费（绑定 attachmentId/objectKey）。
        var confirmExpires = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TicketMinutes, 1, 60));
        await cache.SetAsync(
                ticketKey,
                consumed with
                {
                    ContentLength = received,
                    ExpiresAtUnixMs = confirmExpires.ToUnixTimeMilliseconds(),
                },
                TimeSpan.FromMinutes(Math.Clamp(_options.TicketMinutes, 1, 60)),
                cancellationToken)
            .ConfigureAwait(false);

        return (true, true, received, info.AttachmentId, shaHex, null);
    }

    public async Task<(bool Ok, long Received, string? Error)> GetUploadProgressAsync(
        long userId, string ticket, CancellationToken cancellationToken = default)
    {
        var info = await cache.GetAsync<AttachmentUploadTicket>(
                AttachmentUploadTicketKeys.Create(ticket), cancellationToken)
            .ConfigureAwait(false);
        if (info is null)
            return (false, 0, "上传票无效或已过期");
        if (info.UserId != userId)
            return (false, 0, "上传票与用户不匹配");
        if (!TryResolveTicketPaths(info, out var fullPath, out var tempPath, out var error))
            return (false, 0, error);

        // 只读探针：无副作用；优先报最终对象长度（定稿后探针仍可用）。
        if (File.Exists(fullPath))
            return (true, new FileInfo(fullPath).Length, null);
        if (File.Exists(tempPath))
            return (true, new FileInfo(tempPath).Length, null);
        return (true, 0, null);
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

    /// <summary>解析票的对象最终路径与 <c>.uploading</c> 临时路径；拒绝越界对象键。</summary>
    private bool TryResolveTicketPaths(
        AttachmentUploadTicket info,
        out string fullPath,
        out string tempPath,
        out string? error)
    {
        var root = EnsureDirectoryBoundary(_options.LocalRootPath);
        fullPath = Path.GetFullPath(Path.Combine(root, info.ObjectKey.Replace('/', Path.DirectorySeparatorChar)));
        tempPath = fullPath + ".uploading";
        if (!IsUnderRoot(root, fullPath))
        {
            error = "非法对象键";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>票失效/过期时清理其 partial，防止无人认领的临时文件驻留磁盘。</summary>
    private void TryDeleteTicketPartial(AttachmentUploadTicket info)
    {
        if (!TryResolveTicketPaths(info, out _, out var tempPath, out _))
            return;
        TryDeleteFile(tempPath);
    }

    private static string? GetHashHex(IncrementalHash hasher)
    {
        Span<byte> hash = stackalloc byte[32];
        return hasher.TryGetHashAndReset(hash, out var hashLength)
               && hashLength == hash.Length
            ? Convert.ToHexStringLower(hash)
            : null;
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

        AttachmentUploadTicket? info = null;
        if (!string.IsNullOrWhiteSpace(ticket))
        {
            var ticketKey = AttachmentUploadTicketKeys.Create(ticket);
            info = await atomicCache.TryGetAndDeleteAsync<AttachmentUploadTicket>(ticketKey, cancellationToken)
                .ConfigureAwait(false);
            if (info is null)
            {
                // 普通上传票不存在：尝试秒传去重票（Presign 命中已确认内容时签发）。
                var dedup = await atomicCache.TryGetAndDeleteAsync<AttachmentDedupUploadTicket>(
                        AttachmentUploadTicketKeys.CreateDedup(ticket), cancellationToken)
                    .ConfigureAwait(false);
                if (dedup is not null)
                    return await ConfirmDedupObjectAsync(userId, objectKey, dedup, cancellationToken)
                        .ConfigureAwait(false);
                return (false, null, null, null, null, 0, null, "上传票无效或已过期");
            }
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
                await RestoreTicketAsync(AttachmentUploadTicketKeys.Create(ticket!), info, cancellationToken).ConfigureAwait(false);
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

    private async Task<(bool Ok, string? PublicUrl, string? ObjectKey, string? AttachmentId, string? ContentType, long SizeBytes, string? OriginalName, string? Error)>
        ConfirmDedupObjectAsync(
            long userId,
            string objectKey,
            AttachmentDedupUploadTicket dedup,
            CancellationToken cancellationToken)
    {
        if (dedup.UserId != userId)
            return (false, null, null, null, null, 0, null, "上传票与用户不匹配");
        if (!string.Equals(dedup.ObjectKey, objectKey, StringComparison.Ordinal))
            return (false, null, null, null, null, 0, null, "对象键与上传票不匹配");
        if (!objectKey.StartsWith($"{userId}/", StringComparison.Ordinal))
            return (false, null, null, null, null, 0, null, "无效的附件对象键");

        var root = EnsureDirectoryBoundary(_options.LocalRootPath);
        var sourcePath = Path.GetFullPath(
            Path.Combine(root, dedup.SourceObjectKey.Replace('/', Path.DirectorySeparatorChar)));
        var targetPath = Path.GetFullPath(
            Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(root, sourcePath) || !IsUnderRoot(root, targetPath))
            return (false, null, null, null, null, 0, null, "非法对象键");
        if (!File.Exists(sourcePath))
            return (false, null, null, null, null, 0, null, "内容源对象不存在，秒传失效");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
            var size = new FileInfo(targetPath).Length;
            if (size <= 0 || size > MaxBytes)
            {
                TryDeleteFile(targetPath);
                return (false, null, null, null, null, 0, null, "附件大小超限");
            }

            return (true, string.Empty, objectKey, dedup.AttachmentId,
                dedup.ContentType, size, dedup.OriginalName, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "本地秒传复制失败 AttachmentId={AttachmentId}", dedup.AttachmentId);
            TryDeleteFile(targetPath);
            return (false, null, null, null, null, 0, null, "附件秒传复制失败");
        }
    }

    private static bool IsContentAddress(string sha256Hex) =>
        sha256Hex.Length == 64
        && sha256Hex.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

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
        string ticketKey, AttachmentUploadTicket info, CancellationToken cancellationToken)
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

}
