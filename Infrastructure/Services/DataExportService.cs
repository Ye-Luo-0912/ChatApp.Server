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
    string? Error);

public interface IDataExportBlobStore
{
    Task WriteAsync(string objectKey, Stream content, CancellationToken cancellationToken = default);
    Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// 本地文件导出存储。默认 AES-GCM 信封加密落盘（Security:SecretEncryptionKey）。
/// GAP：生产切 S3 时仍需 SSE-S3/KMS；本实现覆盖本地/过渡态 at-rest。
/// </summary>
public sealed class LocalDataExportBlobStore : IDataExportBlobStore
{
    private static readonly byte[] Magic = "CAE1"u8.ToArray();
    private const int HeaderSize = 4 + 4 + 12 + 16; // magic + keyVer + nonce + tag

    private readonly string _root;
    private readonly bool _encryptAtRest;
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

        await using var plain = new MemoryStream();
        await content.CopyToAsync(plain, cancellationToken).ConfigureAwait(false);
        var plainBytes = plain.ToArray();

        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        if (!_encryptAtRest)
        {
            await fs.WriteAsync(plainBytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[16];
        var aad = Encoding.UTF8.GetBytes(objectKey);
        using (var aes = new AesGcm(_keysByVersion[_currentVersion], 16))
            aes.Encrypt(nonce, plainBytes, cipher, tag, aad);

        var header = new byte[HeaderSize];
        Buffer.BlockCopy(Magic, 0, header, 0, 4);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), _currentVersion);
        Buffer.BlockCopy(nonce, 0, header, 8, 12);
        Buffer.BlockCopy(tag, 0, header, 20, 16);

        await fs.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await fs.WriteAsync(cipher, cancellationToken).ConfigureAwait(false);
    }

    public Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var path = Resolve(objectKey);
        if (!File.Exists(path))
            return Task.FromResult<Stream?>(null);

        var raw = File.ReadAllBytes(path);
        if (raw.Length >= HeaderSize && raw.AsSpan(0, 4).SequenceEqual(Magic))
        {
            var keyVersion = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(4, 4));
            if (!_keysByVersion.TryGetValue(keyVersion, out var key))
                throw new CryptographicException($"Unknown export blob key version {keyVersion}");

            var nonce = raw.AsSpan(8, 12);
            var tag = raw.AsSpan(20, 16);
            var cipher = raw.AsSpan(HeaderSize);
            var plain = new byte[cipher.Length];
            var aad = Encoding.UTF8.GetBytes(objectKey);
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain, aad);
            Stream decrypted = new MemoryStream(plain, writable: false);
            return Task.FromResult<Stream?>(decrypted);
        }

        // 兼容历史明文落盘
        Stream stream = new MemoryStream(raw, writable: false);
        return Task.FromResult<Stream?>(stream);
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
}

/// <summary>数据库持久化的异步导出：租约领取 + 对象存储 + 成功打开后再消费。</summary>
public sealed class DataExportService(
    UserDbContext db,
    ITrustedDeviceService trustedDevices,
    IDataExportBlobStore blobStore,
    IOptions<DataExportStorageOptions> options,
    IServiceScopeFactory scopeFactory) : IDataExportService
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
            userId, password, mfaCode, stepUpToken, cancellationToken).ConfigureAwait(false);
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
            : new DataExportStatusDto(job.Id, job.Status, job.CreatedAt, job.ReadyAt, job.ExpiresAt, job.Error);
    }

    public async Task<(Stream? Stream, string? FileName, string? Error)> OpenDownloadAsync(
        long userId, string jobId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var job = await db.DataExportJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, cancellationToken)
            .ConfigureAwait(false);
        if (job is null) return (null, null, "作业不存在");
        if (job.ConsumedAt is not null || job.Status == DataExportJobStatus.Consumed)
            return (null, null, "下载链接已使用");
        if (job.Status == DataExportJobStatus.Expired
            || (job.ExpiresAt is { } exp && exp < now))
            return (null, null, "导出已过期");
        if (job.Status != DataExportJobStatus.Ready || string.IsNullOrWhiteSpace(job.ObjectKey))
            return (null, null, "导出尚未就绪");

        // 先打开 blob；失败则保持 Ready，允许重试。
        var stream = await blobStore.OpenReadAsync(job.ObjectKey, cancellationToken).ConfigureAwait(false);
        if (stream is null)
            return (null, null, "导出文件缺失");

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
            return (null, null, "下载链接已使用");
        }

        // 流交给调用方；Dispose 时用新 Scope 删 blob，失败则墓碑 PendingDelete。
        var objectKey = job.ObjectKey!;
        return (new ExportDownloadStream(stream, async () =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var scopedDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var scopedBlob = scope.ServiceProvider.GetRequiredService<IDataExportBlobStore>();
            await TryDeleteBlobOrTombstoneAsync(
                    scopedDb, scopedBlob, jobId, objectKey, "download", removeRowOnSuccess: false)
                .ConfigureAwait(false);
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

    /// <summary>下载流：Dispose 时再删 blob，避免打开失败却已消费。</summary>
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
                onDisposed().GetAwaiter().GetResult();
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
            await ProcessJobAsync(db, blob, sessions, job.Id, job.UserId, opts, _instanceId, leaseSeconds, cancellationToken)
                .ConfigureAwait(false);
            AuthSecurityMetrics.ExportFinished("ready", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "导出作业失败 JobId={JobId}", job.Id);
            await db.DataExportJobs
                .Where(j => j.Id == job.Id && j.LeaseOwner == _instanceId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(j => j.Status, DataExportJobStatus.Failed)
                        .SetProperty(j => j.Error, ex.Message.Length > 500 ? ex.Message[..500] : ex.Message)
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
        string jobId,
        long userId,
        DataExportStorageOptions opts,
        string leaseOwner,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        await RenewLeaseAsync(db, jobId, leaseOwner, leaseSeconds, cancellationToken).ConfigureAwait(false);

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
                   ?? throw new InvalidOperationException("用户不存在");

        var friendIds = await db.Friendships.AsNoTracking()
            .Where(f => f.UserId == userId).Select(f => f.FriendId).ToListAsync(cancellationToken);
        await RenewLeaseAsync(db, jobId, leaseOwner, leaseSeconds, cancellationToken).ConfigureAwait(false);

        var events = await db.SecurityEvents.AsNoTracking()
            .Where(e => e.UserId == userId).OrderByDescending(e => e.Id).Take(2000)
            .Select(e => new { e.Id, e.EventType, e.DeviceId, e.ClientIp, e.Location, e.Detail, e.CreatedAt })
            .ToListAsync(cancellationToken);
        var notifications = await db.InAppNotifications.AsNoTracking()
            .Where(n => n.UserId == userId).OrderByDescending(n => n.Id).Take(2000)
            .Select(n => new { n.Id, n.Type, n.Title, n.Body, n.IsRead, n.CreatedAt })
            .ToListAsync(cancellationToken);
        await RenewLeaseAsync(db, jobId, leaseOwner, leaseSeconds, cancellationToken).ConfigureAwait(false);

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
                writer.WriteString("note", "消息正文由 Realtime 服务另行导出；本包不含聊天原文。");
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await RenewLeaseAsync(db, jobId, leaseOwner, leaseSeconds, cancellationToken).ConfigureAwait(false);

            await using (var read = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
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
