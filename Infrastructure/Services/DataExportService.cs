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
using Infrastructure.Diagnostics;
using Infrastructure.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// 本地文件导出存储。默认 CAE3 分块 AES-GCM 信封加密落盘（Security:SecretEncryptionKey）。
/// 仍可读遗留 CAE2（零长 EOF）、CAE1 整包密文与明文。生产切 S3 时见 <see cref="DataExportStorageOptions.S3SseMode"/>（SSE-S3/KMS）。
/// </summary>
public sealed class LocalDataExportBlobStore : IDataExportBlobStore, IObjectStoreHealthProbe
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

    public Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_root))
            throw new DirectoryNotFoundException(_root);
        return Task.CompletedTask;
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
            rootPath = "App_Data/exports";
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
/// <summary>数据库持久化的异步导出：租约领取 + 对象存储 + 成功打开后再消费。</summary>
public sealed class DataExportService(
    UserDbContext db,
    ITrustedDeviceService trustedDevices,
    IDataExportBlobStore blobStore,
    IOptions<DataExportStorageOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<DataExportService> logger,
    IAuthSnapshotStore? authSnapshots = null) : IDataExportService
{
    private readonly DataExportStorageOptions _opts = options.Value;
    private readonly IAuthSnapshotStore? _authSnapshots = authSnapshots;

    public async Task<(AuthOperationResult Result, string? JobId)> EnqueueAsync(
        long userId,
        string? password,
        string? mfaCode,
        string? stepUpToken,
        CancellationToken cancellationToken = default)
    {
        if (!await IsAuthoritativelyAllowedAsync(userId, cancellationToken).ConfigureAwait(false))
            return (AuthOperationResult.Fail("AccountUnavailable", "账号当前不可执行导出"), null);

        var step = await trustedDevices.VerifyStepUpAsync(
            userId, password, mfaCode, stepUpToken, StepUpPurposes.DataExport, cancellationToken)
            .ConfigureAwait(false);
        if (!step.Succeeded)
            return (step, null);

        // The partial unique index is intentionally based on lifecycle status,
        // not wall-clock expiry. Release an expired Ready row in the same
        // transaction as the new insert, otherwise the UX query can see no
        // active row while the index still rejects the insert.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var now = DateTimeOffset.UtcNow;
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                : null;
            try
            {
                await db.DataExportJobs
                    .Where(j => j.UserId == userId
                                && j.Status == DataExportJobStatus.Ready
                                && j.ConsumedAt == null
                                && j.ExpiresAt != null
                                && j.ExpiresAt <= now)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(j => j.Status, DataExportJobStatus.Expired)
                            .SetProperty(j => j.NextAttemptAt, now)
                            .SetProperty(j => j.Error, (string?)null),
                        cancellationToken)
                    .ConfigureAwait(false);

                var active = await db.DataExportJobs.AsNoTracking()
                    .Where(j => j.UserId == userId
                                && j.ConsumedAt == null
                                && (j.Status == DataExportJobStatus.Pending
                                    || j.Status == DataExportJobStatus.Processing
                                    || j.Status == DataExportJobStatus.CancelRequested
                                    || j.Status == DataExportJobStatus.Ready))
                    .OrderByDescending(j => j.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (active is not null)
                {
                    if (transaction is not null)
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return (AuthOperationResult.Success(), active.Id);
                }

                var jobId = Guid.NewGuid().ToString("N");
                db.DataExportJobs.Add(new DataExportJob
                {
                    Id = jobId,
                    UserId = userId,
                    Status = DataExportJobStatus.Pending,
                    CreatedAt = now,
                    NextAttemptAt = now,
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                AuthSecurityMetrics.ExportEnqueued();
                return (AuthOperationResult.Success(), jobId);
            }
            catch (DbUpdateException ex)
                when (PostgresDbException.IsUniqueViolation(ex, "UX_DataExportJob_OneActive"))
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                if (attempt == 0)
                    continue;

                var raced = await db.DataExportJobs.AsNoTracking()
                    .Where(j => j.UserId == userId
                                && j.ConsumedAt == null
                        && (j.Status == DataExportJobStatus.Pending
                                    || j.Status == DataExportJobStatus.Processing
                                    || j.Status == DataExportJobStatus.CancelRequested
                                    || j.Status == DataExportJobStatus.Ready))
                    .OrderByDescending(j => j.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (raced is not null)
                    return (AuthOperationResult.Success(), raced.Id);
                throw;
            }
        }

        throw new InvalidOperationException("导出作业创建失败");
    }

    public async Task<DataExportStatusDto?> GetStatusAsync(
        long userId, string jobId, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthoritativelyAllowedAsync(userId, cancellationToken).ConfigureAwait(false))
            return null;

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

    public async Task<AuthOperationResult> CancelAsync(
        long userId,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(jobId))
            return AuthOperationResult.Fail("InvalidJob", "导出作业标识无效");

        var pending = await db.DataExportJobs
            .Where(j => j.Id == jobId
                        && j.UserId == userId
                        && j.Status == DataExportJobStatus.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, DataExportJobStatus.Cancelled)
                    .SetProperty(j => j.Error, DataExportJobErrors.Cancelled),
                cancellationToken)
            .ConfigureAwait(false);
        if (pending == 1)
            return AuthOperationResult.Success();

        var processing = await db.DataExportJobs
            .Where(j => j.Id == jobId
                        && j.UserId == userId
                        && j.Status == DataExportJobStatus.Processing)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, DataExportJobStatus.CancelRequested)
                    .SetProperty(j => j.Error, DataExportJobErrors.Cancelled),
                cancellationToken)
            .ConfigureAwait(false);
        if (processing == 1)
            return AuthOperationResult.Success();

        var existing = await db.DataExportJobs.AsNoTracking()
            .Where(j => j.Id == jobId && j.UserId == userId)
            .Select(j => j.Status)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return existing is DataExportJobStatus.Cancelled or DataExportJobStatus.CancelRequested
            ? AuthOperationResult.Success()
            : AuthOperationResult.Fail("NotCancellable", "导出作业不存在或已进入不可取消状态");
    }

    public async Task<(Stream? Stream, string? FileName, string? Error)> OpenDownloadAsync(
        long userId, string jobId, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthoritativelyAllowedAsync(userId, cancellationToken).ConfigureAwait(false))
            return (null, null, DataExportDownloadErrors.JobNotFound);

        var now = DateTimeOffset.UtcNow;
        var job = await db.DataExportJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, cancellationToken)
            .ConfigureAwait(false);
        if (job is null) return (null, null, DataExportDownloadErrors.JobNotFound);
        if (job.ConsumedAt is not null
            || job.Status is DataExportJobStatus.Consumed
                or DataExportJobStatus.ConsumedPendingDelete)
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

        int claimed;
        try
        {
            claimed = await db.DataExportJobs
                .Where(j => j.Id == jobId
                            && j.UserId == userId
                            && j.Status == DataExportJobStatus.Ready
                            && j.ConsumedAt == null
                            && j.ExpiresAt != null
                            && j.ExpiresAt > now
                            && j.ObjectKey != null)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(j => j.ConsumedAt, now)
                        .SetProperty(
                            j => j.Status,
                            DataExportJobStatus.ConsumedPendingDelete)
                        .SetProperty(
                            j => j.DownloadLeaseUntil,
                            now.AddSeconds(Math.Max(30, _opts.DownloadLeaseSeconds))),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // The blob was opened before the durable consume transition. Any
            // database failure must release that stream before the exception
            // escapes, otherwise an S3 response stream/socket leaks.
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        if (claimed == 0)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return (null, null, DataExportDownloadErrors.DownloadConsumed);
        }

        // The durable tombstone exists before the stream is returned. Dispose
        // only shortens the lease; deletion itself is performed by the worker,
        // so process exit cannot lose PII cleanup.
        var objectKey = job.ObjectKey!;
        var shortId = jobId.Length <= 8 ? jobId : jobId[..8];
        return (new ExportDownloadStream(stream, async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var scopedDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
                await scopedDb.DataExportJobs
                    .Where(j => j.Id == jobId
                                && j.Status == DataExportJobStatus.ConsumedPendingDelete
                                && j.ObjectKey == objectKey)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(
                            j => j.DownloadLeaseUntil,
                            DateTimeOffset.UtcNow),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "导出下载完成标记失败 JobId={JobId}", jobId);
            }
        }),
            $"chatapp-export-{userId}-{shortId}.json", null);
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
                job.LeaseToken = null;
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
                        .SetProperty(j => j.LeaseToken, (string?)null)
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

    private async Task<bool> IsAuthoritativelyAllowedAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var snapshot = _authSnapshots is not null
            ? await _authSnapshots.GetAuthoritativeAsync(userId, cancellationToken)
                .ConfigureAwait(false)
            : await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new UserAuthSnapshot
                {
                    UserId = u.Id,
                    SecurityVersion = u.SecurityVersion,
                    AccountState = u.AccountState,
                    LockoutEnabled = u.LockoutEnabled,
                    LockoutEnd = u.LockoutEnd,
                    BanUntil = u.BanUntil,
                    DeletionScheduledAt = u.DeletionScheduledAt,
                })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        return snapshot?.IsAllowedAt(DateTimeOffset.UtcNow) == true;
    }

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
                // ConsumedPendingDelete + DownloadLeaseUntil was persisted
                // before this stream was returned. Synchronous Dispose must
                // not launch an in-process Task: process shutdown may lose it.
                // The cleanup worker reclaims the durable tombstone after the
                // lease expires. Async callers use DisposeAsync above for the
                // immediate lease-shortening fast path.
            }
            base.Dispose(disposing);
        }
    }
}
