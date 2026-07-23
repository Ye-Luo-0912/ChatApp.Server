using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models.Auth;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public interface IDataExportService
{
    Task<(AuthOperationResult Result, string? JobId)> EnqueueAsync(
        long userId,
        string? password,
        string? mfaCode,
        string? stepUpToken,
        CancellationToken cancellationToken = default);

    Task<DataExportStatusDto?> GetStatusAsync(long userId, string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 打开一次性下载流。失败时 <c>Error</c> 为稳定机器码（见 <see cref="DataExportDownloadErrors"/>）。
    /// </summary>
    Task<(Stream? Stream, string? FileName, string? Error)> OpenDownloadAsync(
        long userId, string jobId, CancellationToken cancellationToken = default);

    /// <summary>账号注销时删除该用户全部导出作业与对象（含 PII）。</summary>
    Task DeleteAllForUserAsync(long userId, CancellationToken cancellationToken = default);
}

public sealed record DataExportStatusDto(
    string JobId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadyAt,
    DateTimeOffset? ExpiresAt,
    /// <summary>公开稳定错误码（Failed 时）；不含异常原文。</summary>
    string? ErrorCode);

public interface IDataExportBlobStore
{
    Task WriteAsync(string objectKey, Stream content, CancellationToken cancellationToken = default);
    Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// 本地文件导出存储。默认 CAE3 分块 AES-GCM 信封加密落盘（Security:SecretEncryptionKey）。
/// 仍可读遗留 CAE2（零长 EOF）、CAE1 整包密文与明文。生产切 S3 时见 <see cref="DataExportStorageOptions.S3SseMode"/>（SSE-S3/KMS）。
/// </summary>
public sealed class LocalDataExportBlobStore : IDataExportBlobStore
{
    private static readonly byte[] MagicCae1 = "CAE1"u8.ToArray();
    private static readonly byte[] MagicCae2 = "CAE2"u8.ToArray();
    private static readonly byte[] MagicCae3 = "CAE3"u8.ToArray();
    private const int Cae1HeaderSize = 4 + 4 + 12 + 16; // magic + keyVer + nonce + tag
    private const int CaeChunkHeaderSize = 4 + 4 + 4; // magic + keyVer + chunkPlainBytes
    private const int EofTrailerPlainBytes = 16; // chunkCount u64 + totalPlainLength u64
    private const int DefaultChunkPlainBytes = 64 * 1024;

    private readonly string _root;
    private readonly bool _encryptAtRest;
    private readonly int _chunkPlainBytes;
    private readonly Dictionary<int, byte[]> _keysByVersion;
    private readonly int _currentVersion;

    public LocalDataExportBlobStore(
        IOptions<DataExportStorageOptions> options,
        IOptions<SecurityOptions> security,
        IOptions<JwtSettings> jwt,
        IHostEnvironment env,
        ILogger<LocalDataExportBlobStore> logger)
    {
        var exportOpts = options.Value;
        _root = EnsureRoot(exportOpts.LocalRootPath);
        _encryptAtRest = exportOpts.EncryptAtRest;
        _chunkPlainBytes = Math.Clamp(
            exportOpts.EncryptChunkBytes <= 0 ? DefaultChunkPlainBytes : exportOpts.EncryptChunkBytes,
            4 * 1024,
            1024 * 1024);

        var sec = security.Value;
        _currentVersion = sec.KeyVersion <= 0 ? 1 : sec.KeyVersion;
        _keysByVersion = new Dictionary<int, byte[]>();

        var primary = sec.SecretEncryptionKey;
        if (string.IsNullOrWhiteSpace(primary))
        {
            if (!env.IsDevelopment() && !env.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException(
                    "生产环境必须配置 Security:SecretEncryptionKey（导出落盘加密），禁止回退到 JwtSettings.Secret");
            }

            primary = string.IsNullOrWhiteSpace(jwt.Value.Secret)
                ? "dev-only-export-encryption-key-change-me"
                : jwt.Value.Secret;
            logger.LogWarning("Security:SecretEncryptionKey 未配置，导出 blob 加密已临时回退（仅 Development/Testing）");
        }

        _keysByVersion[_currentVersion] = Derive(primary);

        if (!string.IsNullOrWhiteSpace(sec.PreviousSecretEncryptionKey)
            && sec.PreviousKeyVersion is { } prevVer
            && prevVer > 0
            && prevVer != _currentVersion)
        {
            _keysByVersion[prevVer] = Derive(sec.PreviousSecretEncryptionKey);
        }
    }

    public async Task WriteAsync(string objectKey, Stream content, CancellationToken cancellationToken = default)
    {
        var path = Resolve(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var fs = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (!_encryptAtRest)
        {
            await content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            return;
        }

        // CAE3：分块 AES-GCM + 认证 EOF（chunkCount / totalPlainLength），流式写入。
        var header = new byte[CaeChunkHeaderSize];
        Buffer.BlockCopy(MagicCae3, 0, header, 0, 4);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), _currentVersion);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8, 4), _chunkPlainBytes);
        await fs.WriteAsync(header, cancellationToken).ConfigureAwait(false);

        var plainBuf = ArrayPool<byte>.Shared.Rent(_chunkPlainBytes);
        var cipherBuf = ArrayPool<byte>.Shared.Rent(_chunkPlainBytes);
        var tag = new byte[16];
        var frameMeta = new byte[16]; // nonce(12) + len(4)
        var aadBase = Encoding.UTF8.GetBytes(objectKey);
        var chunkAad = new byte[aadBase.Length + 8];
        Buffer.BlockCopy(aadBase, 0, chunkAad, 0, aadBase.Length);
        var eofAad = BuildEofAad(aadBase);
        ulong chunkIndex = 0;
        ulong totalPlainLength = 0;
        using var aes = new AesGcm(_keysByVersion[_currentVersion], 16);

        try
        {
            while (true)
            {
                var read = await ReadChunkAsync(content, plainBuf.AsMemory(0, _chunkPlainBytes), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                var nonce = frameMeta.AsSpan(0, 12);
                RandomNumberGenerator.Fill(nonce);
                BinaryPrimitives.WriteUInt32BigEndian(frameMeta.AsSpan(12, 4), (uint)read);
                BinaryPrimitives.WriteUInt64BigEndian(chunkAad.AsSpan(aadBase.Length, 8), chunkIndex);
                aes.Encrypt(
                    nonce,
                    plainBuf.AsSpan(0, read),
                    cipherBuf.AsSpan(0, read),
                    tag,
                    chunkAad);

                await fs.WriteAsync(frameMeta, cancellationToken).ConfigureAwait(false);
                await fs.WriteAsync(cipherBuf.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await fs.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
                chunkIndex++;
                totalPlainLength += (ulong)read;
            }

            // 认证 EOF：len=0 + AES-GCM(chunkCount || totalPlainLength)
            var eofPlain = new byte[EofTrailerPlainBytes];
            BinaryPrimitives.WriteUInt64BigEndian(eofPlain.AsSpan(0, 8), chunkIndex);
            BinaryPrimitives.WriteUInt64BigEndian(eofPlain.AsSpan(8, 8), totalPlainLength);
            var eofCipher = new byte[EofTrailerPlainBytes];
            RandomNumberGenerator.Fill(frameMeta.AsSpan(0, 12));
            BinaryPrimitives.WriteUInt32BigEndian(frameMeta.AsSpan(12, 4), 0);
            aes.Encrypt(frameMeta.AsSpan(0, 12), eofPlain, eofCipher, tag, eofAad);
            await fs.WriteAsync(frameMeta, cancellationToken).ConfigureAwait(false);
            await fs.WriteAsync(eofCipher, cancellationToken).ConfigureAwait(false);
            await fs.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBuf.AsSpan(0, _chunkPlainBytes));
            CryptographicOperations.ZeroMemory(cipherBuf.AsSpan(0, _chunkPlainBytes));
            ArrayPool<byte>.Shared.Return(plainBuf, clearArray: false);
            ArrayPool<byte>.Shared.Return(cipherBuf, clearArray: false);
        }
    }

    public Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(objectKey);
        if (!File.Exists(path))
            return Task.FromResult<Stream?>(null);

        var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

        try
        {
            Span<byte> magic = stackalloc byte[4];
            var n = fs.Read(magic);
            var isCae3 = n == 4 && magic.SequenceEqual(MagicCae3);
            var isCae2 = n == 4 && magic.SequenceEqual(MagicCae2);
            if (isCae3 || isCae2)
            {
                Span<byte> rest = stackalloc byte[8];
                if (fs.Read(rest) != 8)
                    throw new CryptographicException(isCae3 ? "Truncated CAE3 header" : "Truncated CAE2 header");

                var keyVersion = BinaryPrimitives.ReadInt32BigEndian(rest[..4]);
                if (!_keysByVersion.TryGetValue(keyVersion, out var key))
                    throw new CryptographicException($"Unknown export blob key version {keyVersion}");

                var chunkPlain = BinaryPrimitives.ReadInt32BigEndian(rest[4..]);
                if (chunkPlain is < 1024 or > 2 * 1024 * 1024)
                    throw new CryptographicException(isCae3 ? "Invalid CAE3 chunk size" : "Invalid CAE2 chunk size");

                // CAE3 要求认证 EOF；CAE2 仍接受遗留零长未认证 EOF。
                Stream streamed = new CaeChunkDecryptStream(
                    fs, key, Encoding.UTF8.GetBytes(objectKey), chunkPlain, requireAuthenticatedEof: isCae3);
                return Task.FromResult<Stream?>(streamed);
            }

            if (n == 4 && magic.SequenceEqual(MagicCae1))
            {
                // 遗留 CAE1：整包解密（兼容已落盘对象）。
                fs.Position = 0;
                using (fs)
                {
                    var raw = new byte[fs.Length];
                    fs.ReadExactly(raw);
                    var keyVersion = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(4, 4));
                    if (!_keysByVersion.TryGetValue(keyVersion, out var key))
                        throw new CryptographicException($"Unknown export blob key version {keyVersion}");

                    var nonce = raw.AsSpan(8, 12);
                    var tag = raw.AsSpan(20, 16);
                    var cipher = raw.AsSpan(Cae1HeaderSize);
                    var plain = new byte[cipher.Length];
                    var aad = Encoding.UTF8.GetBytes(objectKey);
                    using var aes = new AesGcm(key, 16);
                    aes.Decrypt(nonce, cipher, tag, plain, aad);
                    Stream decrypted = new MemoryStream(plain, writable: false);
                    return Task.FromResult<Stream?>(decrypted);
                }
            }

            // 兼容历史明文落盘：从已读 magic 起拼回完整流
            fs.Position = 0;
            return Task.FromResult<Stream?>(fs);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var path = Resolve(objectKey);
        if (!File.Exists(path))
            return Task.CompletedTask;
        File.Delete(path);
        return Task.CompletedTask;
    }

    private string Resolve(string objectKey)
    {
        var full = Path.GetFullPath(Path.Combine(_root, objectKey.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(_root, full);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("非法导出对象键");
        return full;
    }

    private static string EnsureRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            rootPath = Path.Combine(Path.GetTempPath(), "chatapp-exports");
        var full = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(full);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }

    private static byte[] Derive(string material) => SHA256.HashData(Encoding.UTF8.GetBytes(material));

    /// <summary>EOF 帧 AAD：objectKey || "EOF\0"，与数据块 AAD 域隔离。</summary>
    private static byte[] BuildEofAad(byte[] objectKeyUtf8)
    {
        var aad = new byte[objectKeyUtf8.Length + 4];
        Buffer.BlockCopy(objectKeyUtf8, 0, aad, 0, objectKeyUtf8.Length);
        aad[^4] = (byte)'E';
        aad[^3] = (byte)'O';
        aad[^2] = (byte)'F';
        aad[^1] = 0;
        return aad;
    }

    private static async Task<int> ReadChunkAsync(
        Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await source.ReadAsync(buffer[total..], cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
                break;
            total += n;
        }

        return total;
    }

    /// <summary>
    /// CAE2/CAE3 分块解密流。CAE3 要求认证 EOF；截断在完整数据帧后若无已验证 EOF 则失败。
    /// </summary>
    private sealed class CaeChunkDecryptStream : Stream
    {
        private readonly FileStream _fs;
        private readonly AesGcm _aes;
        private readonly byte[] _aadBase;
        private readonly byte[] _chunkAad;
        private readonly byte[] _eofAad;
        private readonly byte[] _plainBuf;
        private readonly byte[] _cipherBuf;
        private readonly bool _poolOwned;
        private readonly int _chunkPlainBytes;
        private readonly byte[] _tag = new byte[16];
        private readonly byte[] _frameMeta = new byte[16];
        private readonly byte[] _eofCipher = new byte[EofTrailerPlainBytes];
        private readonly byte[] _eofPlain = new byte[EofTrailerPlainBytes];
        private readonly bool _requireAuthenticatedEof;
        private int _plainPos;
        private int _plainLen;
        private ulong _chunkIndex;
        private ulong _totalPlain;
        private bool _eof;
        private bool _disposed;

        public CaeChunkDecryptStream(
            FileStream fs, byte[] key, byte[] aadBase, int chunkPlainBytes, bool requireAuthenticatedEof)
        {
            _fs = fs;
            _aes = new AesGcm(key, 16);
            _aadBase = aadBase;
            _chunkAad = new byte[aadBase.Length + 8];
            Buffer.BlockCopy(aadBase, 0, _chunkAad, 0, aadBase.Length);
            _eofAad = BuildEofAad(aadBase);
            _chunkPlainBytes = chunkPlainBytes;
            _plainBuf = ArrayPool<byte>.Shared.Rent(chunkPlainBytes);
            _cipherBuf = ArrayPool<byte>.Shared.Rent(chunkPlainBytes);
            _poolOwned = true;
            _requireAuthenticatedEof = requireAuthenticatedEof;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.Length == 0)
                return 0;

            var written = 0;
            while (written < buffer.Length)
            {
                if (_plainPos >= _plainLen)
                {
                    if (_eof || !await FillNextChunkAsync(cancellationToken).ConfigureAwait(false))
                        break;
                }

                var n = Math.Min(buffer.Length - written, _plainLen - _plainPos);
                _plainBuf.AsSpan(_plainPos, n).CopyTo(buffer.Span[written..]);
                _plainPos += n;
                written += n;
            }

            return written;
        }

        private async Task<bool> FillNextChunkAsync(CancellationToken cancellationToken)
        {
            _plainPos = 0;
            _plainLen = 0;

            var metaRead = await ReadExactAsync(_fs, _frameMeta, cancellationToken).ConfigureAwait(false);
            if (metaRead == 0)
            {
                // 软 EOF（文件在完整帧后截断）在 CAE3 下不可接受。
                if (_requireAuthenticatedEof)
                    throw new CryptographicException("Missing authenticated CAE3 EOF");
                _eof = true;
                return false;
            }

            if (metaRead != 16)
                throw new CryptographicException("Truncated CAE frame header");

            var cipherLen = BinaryPrimitives.ReadUInt32BigEndian(_frameMeta.AsSpan(12, 4));
            if (cipherLen == 0)
            {
                if (_requireAuthenticatedEof)
                {
                    await VerifyAuthenticatedEofAsync(cancellationToken).ConfigureAwait(false);
                    _eof = true;
                    return false;
                }

                // 遗留 CAE2：零长未认证 EOF。
                _eof = true;
                return false;
            }

            if (cipherLen > (uint)_chunkPlainBytes)
                throw new CryptographicException("CAE frame exceeds chunk size");

            var len = (int)cipherLen;
            if (await ReadExactAsync(_fs, _cipherBuf.AsMemory(0, len), cancellationToken).ConfigureAwait(false) != len
                || await ReadExactAsync(_fs, _tag, cancellationToken).ConfigureAwait(false) != 16)
                throw new CryptographicException("Truncated CAE frame");

            BinaryPrimitives.WriteUInt64BigEndian(_chunkAad.AsSpan(_aadBase.Length, 8), _chunkIndex);
            _aes.Decrypt(
                _frameMeta.AsSpan(0, 12),
                _cipherBuf.AsSpan(0, len),
                _tag,
                _plainBuf.AsSpan(0, len),
                _chunkAad);

            _plainLen = len;
            _chunkIndex++;
            _totalPlain += (ulong)len;
            return true;
        }

        private async Task VerifyAuthenticatedEofAsync(CancellationToken cancellationToken)
        {
            if (await ReadExactAsync(_fs, _eofCipher, cancellationToken).ConfigureAwait(false) != EofTrailerPlainBytes
                || await ReadExactAsync(_fs, _tag, cancellationToken).ConfigureAwait(false) != 16)
                throw new CryptographicException("Truncated CAE3 authenticated EOF");

            _aes.Decrypt(_frameMeta.AsSpan(0, 12), _eofCipher, _tag, _eofPlain, _eofAad);

            var claimedChunks = BinaryPrimitives.ReadUInt64BigEndian(_eofPlain.AsSpan(0, 8));
            var claimedPlain = BinaryPrimitives.ReadUInt64BigEndian(_eofPlain.AsSpan(8, 8));
            if (claimedChunks != _chunkIndex || claimedPlain != _totalPlain)
                throw new CryptographicException("CAE3 EOF trailer mismatch");
        }

        private static async Task<int> ReadExactAsync(
            Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var n = await source.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
                if (n == 0)
                    return total;
                total += n;
            }

            return total;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void ReleaseBuffers()
        {
            if (!_poolOwned) return;
            CryptographicOperations.ZeroMemory(_plainBuf.AsSpan(0, _chunkPlainBytes));
            CryptographicOperations.ZeroMemory(_cipherBuf.AsSpan(0, _chunkPlainBytes));
            ArrayPool<byte>.Shared.Return(_plainBuf, clearArray: false);
            ArrayPool<byte>.Shared.Return(_cipherBuf, clearArray: false);
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                _aes.Dispose();
                ReleaseBuffers();
                _fs.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _aes.Dispose();
            ReleaseBuffers();
            await _fs.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>导出下载稳定错误码（OpenDownloadAsync.Error）。</summary>
public static class DataExportDownloadErrors
{
    public const string JobNotFound = "job_not_found";
    public const string DownloadConsumed = "download_consumed";
    public const string Expired = "expired";
    public const string NotReady = "not_ready";
    public const string BlobMissing = "blob_missing";
}

/// <summary>导出作业 Failed 时对客户端公开的稳定错误码（存于 DataExportJob.Error / StatusDto.ErrorCode）。</summary>
public static class DataExportJobErrors
{
    public const string ExportFailed = "export_failed";
    public const string UserNotFound = "user_not_found";
    public const string LeaseLost = "lease_lost";
    public const string ChatSourceFailed = "chat_source_failed";

    public static string MapPublicCode(Exception ex) => ex switch
    {
        InvalidOperationException ioe when ioe.Message.Contains("用户不存在", StringComparison.Ordinal)
            => UserNotFound,
        InvalidOperationException ioe when ioe.Message.Contains("租约", StringComparison.Ordinal)
            => LeaseLost,
        InvalidOperationException ioe when ioe.Message.Contains("Realtime 历史查询失败", StringComparison.Ordinal)
            => ChatSourceFailed,
        _ => ExportFailed,
    };

    public static string? ToPublicErrorCode(string status, string? stored)
    {
        if (!string.Equals(status, DataExportJobStatus.Failed, StringComparison.Ordinal))
            return null;
        if (string.IsNullOrWhiteSpace(stored))
            return ExportFailed;
        return stored switch
        {
            ExportFailed or UserNotFound or LeaseLost or ChatSourceFailed => stored,
            _ => ExportFailed, // 历史异常原文等一律折叠
        };
    }
}


/// <summary>数据库持久化的异步导出：租约领取 + 对象存储 + 成功打开后再消费。</summary>
public sealed class DataExportService(
    UserDbContext db,
    ITrustedDeviceService trustedDevices,
    IDataExportBlobStore blobStore,
    IOptions<DataExportStorageOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<DataExportService> logger) : IDataExportService
{
    private readonly DataExportStorageOptions _opts = options.Value;

    public async Task<(AuthOperationResult Result, string? JobId)> EnqueueAsync(
        long userId,
        string? password,
        string? mfaCode,
        string? stepUpToken,
        CancellationToken cancellationToken = default)
    {
        var step = await trustedDevices.VerifyStepUpAsync(
            userId, password, mfaCode, stepUpToken, StepUpPurposes.DataExport, cancellationToken)
            .ConfigureAwait(false);
        if (!step.Succeeded)
            return (step, null);

        var now = DateTimeOffset.UtcNow;
        var active = await db.DataExportJobs.AsNoTracking()
            .Where(j => j.UserId == userId
                        && j.ConsumedAt == null
                        && (j.Status == DataExportJobStatus.Pending
                            || j.Status == DataExportJobStatus.Processing
                            || j.Status == DataExportJobStatus.Ready)
                        && (j.ExpiresAt == null || j.ExpiresAt > now))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (active is not null)
            return (AuthOperationResult.Success(), active.Id);

        var jobId = Guid.NewGuid().ToString("N");
        db.DataExportJobs.Add(new DataExportJob
        {
            Id = jobId,
            UserId = userId,
            Status = DataExportJobStatus.Pending,
            CreatedAt = now,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (PostgresDbException.IsUniqueViolation(ex, "UX_DataExportJob_OneActive"))
        {
            db.ChangeTracker.Clear();
            var raced = await db.DataExportJobs.AsNoTracking()
                .Where(j => j.UserId == userId
                            && j.ConsumedAt == null
                            && (j.Status == DataExportJobStatus.Pending
                                || j.Status == DataExportJobStatus.Processing
                                || j.Status == DataExportJobStatus.Ready)
                            && (j.ExpiresAt == null || j.ExpiresAt > DateTimeOffset.UtcNow))
                .OrderByDescending(j => j.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (raced is not null)
                return (AuthOperationResult.Success(), raced.Id);
            throw;
        }

        AuthSecurityMetrics.ExportEnqueued();
        return (AuthOperationResult.Success(), jobId);
    }

    public async Task<DataExportStatusDto?> GetStatusAsync(
        long userId, string jobId, CancellationToken cancellationToken = default)
    {
        var job = await db.DataExportJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, cancellationToken)
            .ConfigureAwait(false);
        return job is null
            ? null
            : new DataExportStatusDto(
                job.Id,
                job.Status,
                job.CreatedAt,
                job.ReadyAt,
                job.ExpiresAt,
                DataExportJobErrors.ToPublicErrorCode(job.Status, job.Error));
    }

    public async Task<(Stream? Stream, string? FileName, string? Error)> OpenDownloadAsync(
        long userId, string jobId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var job = await db.DataExportJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, cancellationToken)
            .ConfigureAwait(false);
        if (job is null) return (null, null, DataExportDownloadErrors.JobNotFound);
        if (job.ConsumedAt is not null || job.Status == DataExportJobStatus.Consumed)
            return (null, null, DataExportDownloadErrors.DownloadConsumed);
        if (job.Status == DataExportJobStatus.Expired
            || (job.ExpiresAt is { } exp && exp < now))
            return (null, null, DataExportDownloadErrors.Expired);
        if (job.Status != DataExportJobStatus.Ready || string.IsNullOrWhiteSpace(job.ObjectKey))
            return (null, null, DataExportDownloadErrors.NotReady);

        // 先打开 blob；失败则保持 Ready，允许重试。
        var stream = await blobStore.OpenReadAsync(job.ObjectKey, cancellationToken).ConfigureAwait(false);
        if (stream is null)
            return (null, null, DataExportDownloadErrors.BlobMissing);

        var claimed = await db.DataExportJobs
            .Where(j => j.Id == jobId
                        && j.UserId == userId
                        && j.Status == DataExportJobStatus.Ready
                        && j.ConsumedAt == null
                        && j.ExpiresAt != null
                        && j.ExpiresAt > now
                        && j.ObjectKey != null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.ConsumedAt, now)
                    .SetProperty(j => j.Status, DataExportJobStatus.Consumed),
                cancellationToken)
            .ConfigureAwait(false);
        if (claimed == 0)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return (null, null, DataExportDownloadErrors.DownloadConsumed);
        }

        // 流交给调用方；Dispose 时 fire-and-forget 删 blob（不在请求线程同步阻塞 IO）。
        var objectKey = job.ObjectKey!;
        return (new ExportDownloadStream(stream, async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var scopedDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
                var scopedBlob = scope.ServiceProvider.GetRequiredService<IDataExportBlobStore>();
                await TryDeleteBlobOrTombstoneAsync(
                        scopedDb, scopedBlob, jobId, objectKey, "download", removeRowOnSuccess: false)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "导出下载后删除 blob 失败 JobId={JobId}", jobId);
            }
        }),
            $"chatapp-export-{userId}-{jobId[..8]}.json", null);
    }

    public async Task DeleteAllForUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        var jobs = await db.DataExportJobs
            .Where(j => j.UserId == userId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.ObjectKey))
            {
                db.DataExportJobs.Remove(job);
                continue;
            }

            try
            {
                await blobStore.DeleteAsync(job.ObjectKey, cancellationToken).ConfigureAwait(false);
                AuthSecurityMetrics.ExportBlobDelete("success");
                db.DataExportJobs.Remove(job);
            }
            catch (Exception ex)
            {
                AuthSecurityMetrics.ExportBlobDelete("failed");
                if (job.Status != DataExportJobStatus.PendingDelete)
                    AuthSecurityMetrics.ExportPendingDeleteDelta(1);
                job.Status = DataExportJobStatus.PendingDelete;
                job.Error = TruncateError($"blob_delete_failed:{ex.Message}");
                job.AttemptCount = Math.Max(job.AttemptCount, 1);
                job.ConsumedAt ??= DateTimeOffset.UtcNow;
                job.LeaseOwner = null;
                job.LeaseUntil = null;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task TryDeleteBlobOrTombstoneAsync(
        UserDbContext db,
        IDataExportBlobStore blobStore,
        string jobId,
        string objectKey,
        string source,
        bool removeRowOnSuccess = true)
    {
        try
        {
            await blobStore.DeleteAsync(objectKey, CancellationToken.None).ConfigureAwait(false);
            AuthSecurityMetrics.ExportBlobDelete("success");
            if (removeRowOnSuccess)
            {
                await db.DataExportJobs.Where(j => j.Id == jobId)
                    .ExecuteDeleteAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                // 下载成功后保留 Consumed 元数据（便于“已使用”提示），仅清除 ObjectKey。
                await db.DataExportJobs.Where(j => j.Id == jobId)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(j => j.ObjectKey, (string?)null)
                            .SetProperty(j => j.Error, (string?)null),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AuthSecurityMetrics.ExportBlobDelete("failed");
            var updated = await db.DataExportJobs
                .Where(j => j.Id == jobId && j.Status != DataExportJobStatus.PendingDelete)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(j => j.Status, DataExportJobStatus.PendingDelete)
                        .SetProperty(j => j.Error, TruncateError($"blob_delete_failed:{source}:{ex.Message}"))
                        .SetProperty(j => j.ObjectKey, objectKey)
                        .SetProperty(j => j.LeaseOwner, (string?)null)
                        .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (updated > 0)
                AuthSecurityMetrics.ExportPendingDeleteDelta(1);
            else
            {
                await db.DataExportJobs
                    .Where(j => j.Id == jobId)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(j => j.Error, TruncateError($"blob_delete_failed:{source}:{ex.Message}"))
                            .SetProperty(j => j.AttemptCount, j => j.AttemptCount + 1),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private static string TruncateError(string message)
        => message.Length <= 500 ? message : message[..500];

    /// <summary>下载流：Dispose 时再删 blob；同步 Dispose 不阻塞等待异步删除。</summary>
    private sealed class ExportDownloadStream(Stream inner, Func<Task> onDisposed) : Stream
    {
        private int _disposed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            await inner.DisposeAsync().ConfigureAwait(false);
            await onDisposed().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (disposing)
            {
                inner.Dispose();
                // 禁止 GetAwaiter().GetResult() 阻塞请求线程；后台删除 + 失败已由回调打日志/墓碑。
                _ = Task.Run(async () =>
                {
                    try { await onDisposed().ConfigureAwait(false); }
                    catch { /* logged inside callback */ }
                });
            }
            base.Dispose(disposing);
        }
    }
}

public sealed class DataExportWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<DataExportStorageOptions> options,
    ILogger<DataExportWorker> logger) : BackgroundService
{
    private readonly string _instanceId = $"{Environment.MachineName}:{Guid.NewGuid():N}"[..32];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = Math.Max(500, options.Value.PollIntervalMilliseconds);
        var cleanupEvery = TimeSpan.FromMinutes(Math.Max(1, options.Value.CleanupIntervalMinutes));
        var nextCleanup = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextCleanup)
                {
                    await CleanupExpiredAsync(stoppingToken).ConfigureAwait(false);
                    nextCleanup = DateTimeOffset.UtcNow + cleanupEvery;
                }

                var claimed = await ClaimAndProcessAsync(stoppingToken).ConfigureAwait(false);
                if (!claimed)
                    await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "导出 Worker 循环异常");
                await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task CleanupExpiredAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IDataExportBlobStore>();
        var now = DateTimeOffset.UtcNow;
        var maxDeleteAttempts = Math.Max(1, options.Value.MaxBlobDeleteAttempts);

        await RetryPendingDeletesAsync(db, blob, maxDeleteAttempts, cancellationToken).ConfigureAwait(false);

        var expired = await db.DataExportJobs.AsNoTracking()
            .Where(j => (j.ExpiresAt != null && j.ExpiresAt < now
                         && (j.Status == DataExportJobStatus.Ready || j.Status == DataExportJobStatus.Consumed))
                        || j.Status == DataExportJobStatus.Expired)
            .OrderBy(j => j.CreatedAt)
            .Take(100)
            .Select(j => new { j.Id, j.ObjectKey })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var job in expired)
        {
            if (string.IsNullOrWhiteSpace(job.ObjectKey))
            {
                await db.DataExportJobs.Where(j => j.Id == job.Id)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            await DataExportService.TryDeleteBlobOrTombstoneAsync(
                    db, blob, job.Id, job.ObjectKey, "cleanup")
                .ConfigureAwait(false);
        }

        // Ready 但已过期：先标 Expired 再下一轮删（避免与下载竞态长时间占位）
        await db.DataExportJobs
            .Where(j => j.Status == DataExportJobStatus.Ready
                        && j.ExpiresAt != null
                        && j.ExpiresAt < now
                        && j.ConsumedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, DataExportJobStatus.Expired),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RetryPendingDeletesAsync(
        UserDbContext db,
        IDataExportBlobStore blob,
        int maxDeleteAttempts,
        CancellationToken cancellationToken)
    {
        var tombs = await db.DataExportJobs
            .Where(j => j.Status == DataExportJobStatus.PendingDelete && j.ObjectKey != null)
            .OrderBy(j => j.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var job in tombs)
        {
            try
            {
                await blob.DeleteAsync(job.ObjectKey!, cancellationToken).ConfigureAwait(false);
                AuthSecurityMetrics.ExportBlobDelete("retry_success");
                AuthSecurityMetrics.ExportPendingDeleteDelta(-1);
                db.DataExportJobs.Remove(job);
            }
            catch (Exception ex)
            {
                AuthSecurityMetrics.ExportBlobDelete("retry_failed");
                job.AttemptCount++;
                job.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                if (job.AttemptCount >= maxDeleteAttempts)
                {
                    logger.LogError(
                        ex,
                        "导出 blob 删除重试耗尽 JobId={JobId}；ObjectKey={ObjectKey}；Attempts={Attempts}",
                        job.Id,
                        job.ObjectKey,
                        job.AttemptCount);
                }
                else
                {
                    logger.LogWarning(
                        ex,
                        "导出 blob 删除重试失败 JobId={JobId}；Attempts={Attempts}",
                        job.Id,
                        job.AttemptCount);
                }
            }
        }

        if (tombs.Count > 0)
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ClaimAndProcessAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IDataExportBlobStore>();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionStore>();
        var chatExport = scope.ServiceProvider.GetRequiredService<IRealtimeChatExportReader>();
        var attachmentMeta = scope.ServiceProvider.GetRequiredService<IAttachmentMetadataStore>();
        var opts = options.Value;
        var now = DateTimeOffset.UtcNow;
        var leaseSeconds = Math.Max(30, opts.LeaseSeconds);
        var leaseUntil = now.AddSeconds(leaseSeconds);

        var job = await db.DataExportJobs
            .Where(j => j.Status == DataExportJobStatus.Pending
                        || (j.Status == DataExportJobStatus.Processing
                            && (j.LeaseUntil == null || j.LeaseUntil < now)))
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (job is null)
            return false;

        var claimed = await db.DataExportJobs
            .Where(j => j.Id == job.Id
                        && (j.Status == DataExportJobStatus.Pending
                            || (j.Status == DataExportJobStatus.Processing
                                && (j.LeaseUntil == null || j.LeaseUntil < now))))
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, DataExportJobStatus.Processing)
                    .SetProperty(j => j.LeaseOwner, _instanceId)
                    .SetProperty(j => j.LeaseUntil, leaseUntil)
                    .SetProperty(j => j.AttemptCount, j => j.AttemptCount + 1),
                cancellationToken)
            .ConfigureAwait(false);
        if (claimed == 0)
            return false;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await ProcessJobAsync(
                    db, blob, sessions, chatExport, attachmentMeta, scopeFactory, job.Id, job.UserId, opts, _instanceId, leaseSeconds, cancellationToken)
                .ConfigureAwait(false);
            AuthSecurityMetrics.ExportFinished("ready", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "导出作业失败 JobId={JobId}", job.Id);
            var publicCode = DataExportJobErrors.MapPublicCode(ex);
            await db.DataExportJobs
                .Where(j => j.Id == job.Id && j.LeaseOwner == _instanceId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(j => j.Status, DataExportJobStatus.Failed)
                        .SetProperty(j => j.Error, publicCode)
                        .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                        .SetProperty(j => j.LeaseOwner, (string?)null),
                    cancellationToken)
                .ConfigureAwait(false);
            AuthSecurityMetrics.ExportFinished("failed", sw.Elapsed.TotalMilliseconds);
        }

        return true;
    }

    private static async Task ProcessJobAsync(
        UserDbContext db,
        IDataExportBlobStore blob,
        ISessionStore sessions,
        IRealtimeChatExportReader chatExport,
        IAttachmentMetadataStore attachmentMeta,
        IServiceScopeFactory scopeFactory,
        string jobId,
        long userId,
        DataExportStorageOptions opts,
        string leaseOwner,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        // 独立心跳：不依赖阶段边界；长查询/大文件上传期间持续续约（独立 DbContext，避免并发争用）。
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatInterval = TimeSpan.FromSeconds(Math.Max(5, leaseSeconds / 3.0));
        var heartbeat = Task.Run(async () =>
        {
            while (!heartbeatCts.Token.IsCancellationRequested)
            {
                await Task.Delay(heartbeatInterval, heartbeatCts.Token).ConfigureAwait(false);
                await using var scope = scopeFactory.CreateAsyncScope();
                var hbDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
                await RenewLeaseAsync(hbDb, jobId, leaseOwner, leaseSeconds, heartbeatCts.Token)
                    .ConfigureAwait(false);
            }
        }, CancellationToken.None);

        try
        {
            await RenewLeaseAsync(db, jobId, leaseOwner, leaseSeconds, cancellationToken).ConfigureAwait(false);

            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
                       ?? throw new InvalidOperationException("用户不存在");

            var friendIds = await db.Friendships.AsNoTracking()
                .Where(f => f.UserId == userId).Select(f => f.FriendId).ToListAsync(cancellationToken);

            var events = await db.SecurityEvents.AsNoTracking()
                .Where(e => e.UserId == userId).OrderByDescending(e => e.Id).Take(2000)
                .Select(e => new { e.Id, e.EventType, e.DeviceId, e.ClientIp, e.Location, e.Detail, e.CreatedAt })
                .ToListAsync(cancellationToken);
            var notifications = await db.InAppNotifications.AsNoTracking()
                .Where(n => n.UserId == userId).OrderByDescending(n => n.Id).Take(2000)
                .Select(n => new { n.Id, n.Type, n.Title, n.Body, n.IsRead, n.CreatedAt })
                .ToListAsync(cancellationToken);

            var reports = await db.UserReports.AsNoTracking()
                .Where(r => r.ReporterId == userId || r.TargetUserId == userId)
                .OrderByDescending(r => r.Id).Take(500)
                .Select(r => new
                {
                    r.Id, r.ReporterId, r.TargetType, r.TargetUserId, r.TargetMessageId,
                    r.Reason, r.Status, r.CreatedAt,
                })
                .ToListAsync(cancellationToken);
            var devices = await db.TrustedDevices.AsNoTracking()
                .Where(d => d.UserId == userId)
                .Select(d => new { d.Id, d.DeviceIdHint, d.Label, d.ClientIp, d.TrustedAt, d.ExpiresAt, d.RevokedAt })
                .ToListAsync(cancellationToken);
            var sessionList = await sessions.ListSessionsAsync(userId.ToString(), cancellationToken);

            var objectKey = $"{userId}/{jobId}.json";
            var tempPath = Path.Combine(Path.GetTempPath(), $"chatapp-export-{jobId}.json");
            try
            {
                await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                await using (var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteString("exportedAt", DateTimeOffset.UtcNow);
                    writer.WritePropertyName("profile");
                    JsonSerializer.Serialize(writer, new
                    {
                        user.Id, user.UserName, user.Email, user.Signature, user.Region,
                        user.CreatedDate, user.AvatarUrl, user.PhoneNumber,
                    });
                    writer.WritePropertyName("friendIds");
                    JsonSerializer.Serialize(writer, friendIds);
                    writer.WritePropertyName("securityEvents");
                    JsonSerializer.Serialize(writer, events);
                    writer.WritePropertyName("notifications");
                    JsonSerializer.Serialize(writer, notifications);
                    writer.WritePropertyName("reports");
                    JsonSerializer.Serialize(writer, reports);
                    writer.WritePropertyName("trustedDevices");
                    JsonSerializer.Serialize(writer, devices);
                    writer.WritePropertyName("sessions");
                    JsonSerializer.Serialize(writer, sessionList.Select(s => new
                    {
                        s.DeviceId, s.ClientIp, s.LoginAt, s.LastActiveAt, s.DeviceName, s.SessionId,
                    }));

                    await WriteChatExportAsync(writer, chatExport, attachmentMeta, userId, opts, cancellationToken)
                        .ConfigureAwait(false);

                    writer.WriteEndObject();
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var read = new FileStream(
                                 tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                 bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await blob.WriteAsync(objectKey, read, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            }

            var readyAt = DateTimeOffset.UtcNow;
            var ttlHours = Math.Clamp(opts.JobTtlHours, 1, 168);
            // 终态仅当本实例仍持有租约，避免旧 Worker 覆盖新租约持有者。
            var updated = await db.DataExportJobs
                .Where(j => j.Id == jobId && j.LeaseOwner == leaseOwner && j.Status == DataExportJobStatus.Processing)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(j => j.Status, DataExportJobStatus.Ready)
                        .SetProperty(j => j.ReadyAt, readyAt)
                        .SetProperty(j => j.ExpiresAt, readyAt.AddHours(ttlHours))
                        .SetProperty(j => j.ObjectKey, objectKey)
                        .SetProperty(j => j.Error, (string?)null)
                        .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                        .SetProperty(j => j.LeaseOwner, (string?)null),
                    cancellationToken)
                .ConfigureAwait(false);
            if (updated == 0)
                throw new InvalidOperationException("导出完成但租约已易主，丢弃结果");
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
            catch (InvalidOperationException) { /* lease lost during cancel — surface via main path */ }
        }
    }

    internal static async Task WriteChatExportAsync(
        Utf8JsonWriter writer,
        IRealtimeChatExportReader chatExport,
        long userId,
        DataExportStorageOptions opts,
        CancellationToken cancellationToken)
        => await WriteChatExportAsync(
            writer, chatExport, UnavailableAttachmentMetadataStore.Instance, userId, opts, cancellationToken)
            .ConfigureAwait(false);

    internal static async Task WriteChatExportAsync(
        Utf8JsonWriter writer,
        IRealtimeChatExportReader chatExport,
        IAttachmentMetadataStore attachmentMeta,
        long userId,
        DataExportStorageOptions opts,
        CancellationToken cancellationToken)
    {
        if (!opts.IncludeChatContent)
        {
            writer.WritePropertyName("chatExport");
            JsonSerializer.Serialize(writer, new
            {
                status = "skipped",
                reason = "DataExport:IncludeChatContent=false",
            });
            WriteEmptyChatArrays(writer);
            return;
        }

        if (!chatExport.IsAvailable)
        {
            writer.WritePropertyName("chatExport");
            JsonSerializer.Serialize(writer, new
            {
                status = "unavailable",
                reason = chatExport.UnavailableReason,
            });
            WriteEmptyChatArrays(writer);
            return;
        }

        var pageSize = Math.Clamp(opts.ChatExportPageSize, 1, 500);
        var maxMessages = Math.Max(1, opts.ChatExportMaxMessages);
        var maxAttachmentUrls = Math.Max(0, opts.ChatExportMaxAttachmentUrls);
        var urlScanMaxChars = Math.Max(0, opts.ChatExportUrlScanMaxContentChars);
        var seenAttachmentUrls = new HashSet<string>(StringComparer.Ordinal);
        var messageCount = 0;
        var receiptCount = 0;
        var attachmentCount = 0;
        var formalAttachmentCount = 0;
        var truncated = false;
        var attachmentUrlsCapped = false;
        var urlScanSkippedNote = false;

        var receiptsPath = Path.Combine(Path.GetTempPath(), $"chatapp-export-rcpt-{Guid.NewGuid():N}.json");
        var attachmentsPath = Path.Combine(Path.GetTempPath(), $"chatapp-export-att-{Guid.NewGuid():N}.json");
        try
        {
            await using (var receiptsFs = new FileStream(
                             receiptsPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                             bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var attachmentsFs = new FileStream(
                             attachmentsPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                             bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var receiptsWriter = new Utf8JsonWriter(receiptsFs))
            await using (var attachmentsWriter = new Utf8JsonWriter(attachmentsFs))
            {
                receiptsWriter.WriteStartArray();
                attachmentsWriter.WriteStartArray();

                // Formal attachments first (DB rows), then legacy parser fallback de-duped by URL.
                if (attachmentMeta.IsAvailable)
                {
                    try
                    {
                        var formal = await attachmentMeta.ListForExportAsync(
                            userId,
                            maxRows: maxAttachmentUrls > 0 ? maxAttachmentUrls : 50_000,
                            cancellationToken).ConfigureAwait(false);
                        foreach (var row in formal)
                        {
                            if (maxAttachmentUrls > 0 && seenAttachmentUrls.Count >= maxAttachmentUrls)
                            {
                                attachmentUrlsCapped = true;
                                break;
                            }

                            var url = row.PublicUrl;
                            if (string.IsNullOrWhiteSpace(url))
                                url = row.ObjectKey;
                            if (string.IsNullOrWhiteSpace(url) || !seenAttachmentUrls.Add(url))
                                continue;

                            var item = new ChatExportAttachmentItem(
                                MessageId: row.MessageId ?? string.Empty,
                                ReceivedAtMs: row.BoundAtMs ?? row.ConfirmedAtMs ?? row.CreatedAtMs,
                                Url: url,
                                Name: row.OriginalName,
                                ContentType: row.ContentType,
                                SizeBytes: row.SizeBytes,
                                Source: "formal");
                            JsonSerializer.Serialize(attachmentsWriter, item);
                            attachmentCount++;
                            formalAttachmentCount++;
                        }
                    }
                    catch
                    {
                        // Formal source optional; fall through to parser.
                    }
                }

                writer.WritePropertyName("messages");
                writer.WriteStartArray();

                await foreach (var msg in chatExport.ReadMessagesAsync(userId, maxMessages + 1, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    if (messageCount >= maxMessages)
                    {
                        truncated = true;
                        break;
                    }

                    JsonSerializer.Serialize(writer, new
                    {
                        msg.MessageId,
                        msg.ClientMessageId,
                        msg.SenderUserId,
                        msg.ReceiverUserId,
                        msg.Content,
                        msg.ReceivedAtMs,
                        msg.DeliveredAtMs,
                        msg.ReadAtMs,
                    });

                    // 回执写入侧流，避免在内存中缓冲全部 receipts。
                    if (msg.DeliveredAtMs is not null || msg.ReadAtMs is not null)
                    {
                        JsonSerializer.Serialize(receiptsWriter, new
                        {
                            msg.MessageId,
                            msg.DeliveredAtMs,
                            msg.ReadAtMs,
                        });
                        receiptCount++;
                    }

                    var skipUrlScan = attachmentUrlsCapped
                                     || (urlScanMaxChars > 0 && msg.Content.Length > urlScanMaxChars);
                    if (skipUrlScan && !attachmentUrlsCapped && msg.Content.Length > urlScanMaxChars)
                        urlScanSkippedNote = true;

                    if (!attachmentUrlsCapped)
                    {
                        foreach (var att in ChatExportAttachmentParser.Extract(
                                     msg.MessageId, msg.ReceivedAtMs, msg.Content,
                                     urlScanMaxChars, skipUrlScan))
                        {
                            if (maxAttachmentUrls > 0 && seenAttachmentUrls.Count >= maxAttachmentUrls)
                            {
                                attachmentUrlsCapped = true;
                                break;
                            }

                            if (!seenAttachmentUrls.Add(att.Url))
                                continue;
                            JsonSerializer.Serialize(attachmentsWriter, att);
                            attachmentCount++;
                        }
                    }

                    messageCount++;
                    // 长导出期间周期性 flush，配合租约心跳避免缓冲过大。
                    if ((messageCount & 0x1FF) == 0)
                    {
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                        await receiptsWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                        await attachmentsWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                writer.WriteEndArray();
                receiptsWriter.WriteEndArray();
                attachmentsWriter.WriteEndArray();
                await receiptsWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                await attachmentsWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await WriteJsonFilePropertyAsync(writer, "receipts", receiptsPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonFilePropertyAsync(writer, "attachments", attachmentsPath, cancellationToken)
                .ConfigureAwait(false);

            writer.WritePropertyName("chatExport");
            JsonSerializer.Serialize(writer, new
            {
                status = truncated || attachmentUrlsCapped ? "truncated" : "ok",
                messageCount,
                receiptCount,
                attachmentCount,
                formalAttachmentCount,
                pageSize,
                maxMessages,
                truncated,
                attachmentUrlsCapped,
                urlScanSkipped = urlScanSkippedNote,
                note = attachmentUrlsCapped
                    ? "attachment URL dedupe set capped; further URL scan skipped"
                    : urlScanSkippedNote
                        ? "some message bodies exceeded urlScanMaxContentChars; URL scan skipped for those"
                        : (string?)null,
            });
        }
        finally
        {
            try { if (File.Exists(receiptsPath)) File.Delete(receiptsPath); } catch { /* best effort */ }
            try { if (File.Exists(attachmentsPath)) File.Delete(attachmentsPath); } catch { /* best effort */ }
        }
    }

    private static void WriteEmptyChatArrays(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("messages");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WritePropertyName("receipts");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WritePropertyName("attachments");
        writer.WriteStartArray();
        writer.WriteEndArray();
    }

    private static async Task WriteJsonFilePropertyAsync(
        Utf8JsonWriter writer,
        string propertyName,
        string path,
        CancellationToken cancellationToken)
    {
        writer.WritePropertyName(propertyName);
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        // 侧流已是完整 JSON 值（数组）；WriteRawValue 避免再解析进 DOM。
        using var ms = new MemoryStream((int)Math.Min(fs.Length, int.MaxValue));
        await fs.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        writer.WriteRawValue(ms.GetBuffer().AsSpan(0, (int)ms.Length), skipInputValidation: true);
    }

    private static async Task RenewLeaseAsync(
        UserDbContext db,
        string jobId,
        string leaseOwner,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds);
        var n = await db.DataExportJobs
            .Where(j => j.Id == jobId && j.LeaseOwner == leaseOwner && j.Status == DataExportJobStatus.Processing)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseUntil, until), cancellationToken)
            .ConfigureAwait(false);
        if (n == 0)
            throw new InvalidOperationException("导出租约已丢失");
    }
}
