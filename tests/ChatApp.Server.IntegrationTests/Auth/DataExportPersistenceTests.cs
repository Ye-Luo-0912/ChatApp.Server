using System.Text;
using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models.Export;
using Core.Models.Identity;
using Core.Settings;
using Infrastructure.Data;
using Infrastructure.Services;
using Infrastructure.Services.Auth;
using Infrastructure.Services.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisPostgresCollection))]
public sealed class DataExportPersistenceTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [SkippableFact]
    public async Task Export_RequiresStepUp_Persists_AndOneShotDownload()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var password = "Passw0rd!";
        var user = await SeedAsync(db, password);
        var trusted = CreateTrusted(db);

        var root = Path.Combine(Path.GetTempPath(), "chatapp-export-tests", Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new DataExportStorageOptions
        {
            LocalRootPath = root,
            LeaseSeconds = 60,
            PollIntervalMilliseconds = 200,
            JobTtlHours = 24,
            EncryptAtRest = true,
        });
        var blob = CreateBlobStore(opts);
        var export = new DataExportService(db, trusted, blob, opts, new ExportTestScopeFactory(postgres, CreateTokenSessions(), blob, opts), NullLogger<DataExportService>.Instance);

        var denied = await export.EnqueueAsync(user.Id, null, null, null);
        Assert.False(denied.Result.Succeeded);

        var (ok, jobId) = await export.EnqueueAsync(user.Id, password, null, null);
        Assert.True(ok.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(jobId));
        Assert.True(await db.DataExportJobs.AnyAsync(j => j.Id == jobId && j.Status == DataExportJobStatus.Pending));

        var factory = new ExportTestScopeFactory(postgres, CreateTokenSessions(), blob, opts);
        var worker = new DataExportWorker(factory, opts, NullLogger<DataExportWorker>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await worker.StartAsync(cts.Token);

        DataExportJob? ready = null;
        for (var i = 0; i < 50; i++)
        {
            db.ChangeTracker.Clear();
            ready = await db.DataExportJobs.AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == jobId, cts.Token);
            if (ready?.Status is DataExportJobStatus.Ready or DataExportJobStatus.Failed)
                break;
            await Task.Delay(200, cts.Token);
        }

        await worker.StopAsync(CancellationToken.None);
        Assert.NotNull(ready);
        Assert.Equal(DataExportJobStatus.Ready, ready!.Status);
        Assert.False(string.IsNullOrWhiteSpace(ready.ObjectKey));

        var onDisk = await File.ReadAllBytesAsync(Path.Combine(root, ready.ObjectKey!.Replace('/', Path.DirectorySeparatorChar)));
        Assert.True(onDisk.Length >= 4);
        Assert.Equal("CAE3"u8.ToArray(), onDisk.AsSpan(0, 4).ToArray());
        Assert.DoesNotContain("exportedAt"u8.ToArray(), onDisk);

        var (stream, _, err) = await export.OpenDownloadAsync(user.Id, jobId!, CancellationToken.None);
        Assert.Null(err);
        Assert.NotNull(stream);
        await using (stream)
        {
            using var reader = new StreamReader(stream!);
            var json = await reader.ReadToEndAsync();
            Assert.Contains("exportedAt", json, StringComparison.Ordinal);
            Assert.Contains("profile", json, StringComparison.Ordinal);
            Assert.Contains("messages", json, StringComparison.Ordinal);
            Assert.Contains("m-export-1", json, StringComparison.Ordinal);
            Assert.Contains("receipts", json, StringComparison.Ordinal);
            Assert.Contains("attachments", json, StringComparison.Ordinal);
            Assert.Contains("https://cdn.example/a.png", json, StringComparison.Ordinal);
            Assert.Contains("\"status\": \"ok\"", json, StringComparison.Ordinal);
        }

        var (again, _, err2) = await export.OpenDownloadAsync(user.Id, jobId!, CancellationToken.None);
        Assert.Null(again);
        Assert.Equal(DataExportDownloadErrors.DownloadConsumed, err2);
    }

    [SkippableFact]
    public async Task Export_OpenDownload_AllowsRetry_WhenBlobMissing()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var password = "Passw0rd!";
        var user = await SeedAsync(db, password);
        var trusted = CreateTrusted(db);

        var root = Path.Combine(Path.GetTempPath(), "chatapp-export-tests", Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new DataExportStorageOptions
        {
            LocalRootPath = root,
            LeaseSeconds = 60,
            PollIntervalMilliseconds = 200,
            JobTtlHours = 24,
        });
        var blob = CreateBlobStore(opts);
        var export = new DataExportService(db, trusted, blob, opts, new ExportTestScopeFactory(postgres, CreateTokenSessions(), blob, opts), NullLogger<DataExportService>.Instance);

        var jobId = Guid.NewGuid().ToString("N");
        db.DataExportJobs.Add(new DataExportJob
        {
            Id = jobId,
            UserId = user.Id,
            Status = DataExportJobStatus.Ready,
            CreatedAt = DateTimeOffset.UtcNow,
            ReadyAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            ObjectKey = $"{user.Id}/missing.json",
        });
        await db.SaveChangesAsync();

        var (stream, _, err) = await export.OpenDownloadAsync(user.Id, jobId);
        Assert.Null(stream);
        Assert.Equal(DataExportDownloadErrors.BlobMissing, err);

        var still = await db.DataExportJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
        Assert.Equal(DataExportJobStatus.Ready, still.Status);
        Assert.Null(still.ConsumedAt);
    }

    [SkippableFact]
    public async Task DeleteAllForUser_BlobFailure_LeavesPendingDeleteTombstone()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var password = "Passw0rd!";
        var user = await SeedAsync(db, password);
        var trusted = CreateTrusted(db);

        var root = Path.Combine(Path.GetTempPath(), "chatapp-export-tests", Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new DataExportStorageOptions
        {
            LocalRootPath = root,
            LeaseSeconds = 60,
            PollIntervalMilliseconds = 200,
            JobTtlHours = 24,
        });
        var realBlob = CreateBlobStore(opts);
        var objectKey = $"{user.Id}/tombstone.json";
        await using (var ms = new MemoryStream("{}"u8.ToArray()))
            await realBlob.WriteAsync(objectKey, ms);

        var failing = new FailingDeleteBlobStore(realBlob);
        var scopeFactory = new ExportTestScopeFactory(postgres, CreateTokenSessions(), failing, opts);
        var export = new DataExportService(db, trusted, failing, opts, scopeFactory, NullLogger<DataExportService>.Instance);

        var jobId = Guid.NewGuid().ToString("N");
        db.DataExportJobs.Add(new DataExportJob
        {
            Id = jobId,
            UserId = user.Id,
            Status = DataExportJobStatus.Ready,
            CreatedAt = DateTimeOffset.UtcNow,
            ReadyAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            ObjectKey = objectKey,
        });
        await db.SaveChangesAsync();

        await export.DeleteAllForUserAsync(user.Id);

        var tomb = await db.DataExportJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal(DataExportJobStatus.PendingDelete, tomb.Status);
        Assert.Equal(objectKey, tomb.ObjectKey);
        Assert.Contains("blob_delete_failed", tomb.Error ?? "", StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar))));
    }

    [SkippableFact]
    public async Task PendingDelete_WorkerRetry_RemovesBlobAndRow()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var user = await SeedAsync(db, "Passw0rd!");

        var root = Path.Combine(Path.GetTempPath(), "chatapp-export-tests", Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new DataExportStorageOptions
        {
            LocalRootPath = root,
            LeaseSeconds = 60,
            PollIntervalMilliseconds = 100,
            JobTtlHours = 24,
            CleanupIntervalMinutes = 1,
        });
        var blob = CreateBlobStore(opts);
        var objectKey = $"{user.Id}/retry-del.json";
        await using (var ms = new MemoryStream("{}"u8.ToArray()))
            await blob.WriteAsync(objectKey, ms);

        var jobId = Guid.NewGuid().ToString("N");
        db.DataExportJobs.Add(new DataExportJob
        {
            Id = jobId,
            UserId = user.Id,
            Status = DataExportJobStatus.PendingDelete,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            ObjectKey = objectKey,
            Error = "blob_delete_failed:prior",
            AttemptCount = 1,
            ConsumedAt = DateTimeOffset.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();

        var factory = new ExportTestScopeFactory(postgres, CreateTokenSessions(), blob, opts);
        var worker = new DataExportWorker(factory, opts, NullLogger<DataExportWorker>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(cts.Token);

        for (var i = 0; i < 40; i++)
        {
            db.ChangeTracker.Clear();
            if (!await db.DataExportJobs.AnyAsync(j => j.Id == jobId, cts.Token))
                break;
            await Task.Delay(200, cts.Token);
        }

        await worker.StopAsync(CancellationToken.None);
        Assert.False(await db.DataExportJobs.AnyAsync(j => j.Id == jobId));
        Assert.False(File.Exists(Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task Cae3_StreamingEncryptDecrypt_RoundTrip_LargeishFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "chatapp-export-tests", Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new DataExportStorageOptions
        {
            LocalRootPath = root,
            EncryptAtRest = true,
            EncryptChunkBytes = 8 * 1024,
        });
        var blob = CreateBlobStore(opts);
        var objectKey = "u1/stream-roundtrip.json";

        // ~1.5MiB payload; streaming encrypt/decrypt must not require whole file in one byte[].
        var plain = new byte[1_500_000];
        Random.Shared.NextBytes(plain);
        plain[0] = (byte)'{';
        plain[^1] = (byte)'}';

        await using (var input = new MemoryStream(plain, writable: false))
            await blob.WriteAsync(objectKey, input);

        var onDisk = await File.ReadAllBytesAsync(
            Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal("CAE3"u8.ToArray(), onDisk.AsSpan(0, 4).ToArray());
        Assert.True(onDisk.Length > 12);

        await using var decrypted = await blob.OpenReadAsync(objectKey);
        Assert.NotNull(decrypted);
        await using var ms = new MemoryStream();
        await decrypted!.CopyToAsync(ms);
        Assert.Equal(plain, ms.ToArray());
    }

    [Fact]
    public async Task Cae3_TruncateAfterCompleteFrames_OpenReadFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "chatapp-export-tests", Guid.NewGuid().ToString("N"));
        var chunk = 4 * 1024;
        var opts = Options.Create(new DataExportStorageOptions
        {
            LocalRootPath = root,
            EncryptAtRest = true,
            EncryptChunkBytes = chunk,
        });
        var blob = CreateBlobStore(opts);
        var objectKey = "u1/truncate-eof.json";

        // 3 full chunks so truncation after N complete frames is unambiguous.
        var plain = new byte[chunk * 3];
        Random.Shared.NextBytes(plain);

        var path = Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar));
        await using (var input = new MemoryStream(plain, writable: false))
            await blob.WriteAsync(objectKey, input);

        var onDisk = await File.ReadAllBytesAsync(path);
        Assert.Equal("CAE3"u8.ToArray(), onDisk.AsSpan(0, 4).ToArray());

        // Authenticated EOF frame = nonce(12) + len(4) + cipher(16) + tag(16) = 48 bytes.
        const int eofFrameBytes = 12 + 4 + 16 + 16;
        Assert.True(onDisk.Length > eofFrameBytes);
        await File.WriteAllBytesAsync(path, onDisk.AsSpan(0, onDisk.Length - eofFrameBytes).ToArray());

        await using var decrypted = await blob.OpenReadAsync(objectKey);
        Assert.NotNull(decrypted);
        await using var ms = new MemoryStream();
        var ex = await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(
            async () => await decrypted!.CopyToAsync(ms));
        Assert.Contains("EOF", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cae3_RepeatedRoundTrip_AllocSmoke_DoesNotThrow()
    {
        // ????????????? ArrayPool / ?? AesGcm ??????????????
        var root = Path.Combine(Path.GetTempPath(), "chatapp-export-tests", Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new DataExportStorageOptions
        {
            LocalRootPath = root,
            EncryptAtRest = true,
            EncryptChunkBytes = 4 * 1024,
        });
        var blob = CreateBlobStore(opts);

        for (var i = 0; i < 8; i++)
        {
            var objectKey = $"u1/alloc-smoke-{i}.json";
            var plain = Encoding.UTF8.GetBytes($"{{\"n\":{i},\"pad\":\"{new string('x', 9000)}\"}}");
            await using (var input = new MemoryStream(plain, writable: false))
                await blob.WriteAsync(objectKey, input);

            await using var decrypted = await blob.OpenReadAsync(objectKey);
            Assert.NotNull(decrypted);
            await using var ms = new MemoryStream();
            await decrypted!.CopyToAsync(ms);
            Assert.Equal(plain, ms.ToArray());
        }
    }

    [SkippableFact]
    public async Task Export_FailedStatus_ReturnsPublicErrorCode_NotExceptionText()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var db = postgres.CreateContext();
        var password = "Passw0rd!";
        var user = await SeedAsync(db, password);
        var trusted = CreateTrusted(db);
        var root = Path.Combine(Path.GetTempPath(), "chatapp-export-tests", Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new DataExportStorageOptions
        {
            LocalRootPath = root,
            LeaseSeconds = 60,
            PollIntervalMilliseconds = 200,
            JobTtlHours = 24,
        });
        var blob = CreateBlobStore(opts);
        var export = new DataExportService(
            db, trusted, blob, opts,
            new ExportTestScopeFactory(postgres, CreateTokenSessions(), blob, opts),
            NullLogger<DataExportService>.Instance);

        var jobId = Guid.NewGuid().ToString("N");
        db.DataExportJobs.Add(new DataExportJob
        {
            Id = jobId,
            UserId = user.Id,
            Status = DataExportJobStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow,
            Error = "System.InvalidOperationException: secret stack trace /tmp/foo",
        });
        await db.SaveChangesAsync();

        var status = await export.GetStatusAsync(user.Id, jobId);
        Assert.NotNull(status);
        Assert.Equal(DataExportJobStatus.Failed, status!.Status);
        Assert.Equal(DataExportJobErrors.ExportFailed, status.ErrorCode);
        Assert.DoesNotContain("InvalidOperationException", status.ErrorCode ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("stack", status.ErrorCode ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cae2_LegacyZeroLenEof_StillReadable()
    {
        var root = Path.Combine(Path.GetTempPath(), "chatapp-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var objectKey = "u1/legacy-cae2.json";
        var path = Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        const int chunkPlain = 4 * 1024;
        var key = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("test-mfa-encryption-key"));
        var plain = "{\"legacyCae2\":true}"u8.ToArray();
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        var aadBase = System.Text.Encoding.UTF8.GetBytes(objectKey);
        var aad = new byte[aadBase.Length + 8];
        Buffer.BlockCopy(aadBase, 0, aad, 0, aadBase.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(aad.AsSpan(aadBase.Length), 0);
        using (var aes = new System.Security.Cryptography.AesGcm(key, 16))
            aes.Encrypt(nonce, plain, cipher, tag, aad);

        await using (var fs = File.Create(path))
        {
            await fs.WriteAsync("CAE2"u8.ToArray());
            var hdr = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(hdr.AsSpan(0, 4), 1);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(hdr.AsSpan(4, 4), chunkPlain);
            await fs.WriteAsync(hdr);
            var meta = new byte[16];
            nonce.CopyTo(meta, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(12, 4), (uint)plain.Length);
            await fs.WriteAsync(meta);
            await fs.WriteAsync(cipher);
            await fs.WriteAsync(tag);
            // Legacy unauthenticated zero-length EOF
            await fs.WriteAsync(new byte[16]);
        }

        var opts = Options.Create(new DataExportStorageOptions
        {
            LocalRootPath = root,
            EncryptAtRest = true,
            EncryptChunkBytes = chunkPlain,
        });
        var blob = CreateBlobStore(opts);
        await using var stream = await blob.OpenReadAsync(objectKey);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var json = await reader.ReadToEndAsync();
        Assert.Contains("legacyCae2", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cae1_LegacyBlob_StillReadable()
    {
        var root = Path.Combine(Path.GetTempPath(), "chatapp-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var objectKey = "u1/legacy-cae1.json";
        var path = Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var key = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("test-mfa-encryption-key"));
        var plain = "{\"legacy\":true}"u8.ToArray();
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        var aad = System.Text.Encoding.UTF8.GetBytes(objectKey);
        using (var aes = new System.Security.Cryptography.AesGcm(key, 16))
            aes.Encrypt(nonce, plain, cipher, tag, aad);

        await using (var fs = File.Create(path))
        {
            await fs.WriteAsync("CAE1"u8.ToArray());
            var ver = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ver, 1);
            await fs.WriteAsync(ver);
            await fs.WriteAsync(nonce);
            await fs.WriteAsync(tag);
            await fs.WriteAsync(cipher);
        }

        var opts = Options.Create(new DataExportStorageOptions
        {
            LocalRootPath = root,
            EncryptAtRest = true,
        });
        var blob = CreateBlobStore(opts);
        await using var stream = await blob.OpenReadAsync(objectKey);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var json = await reader.ReadToEndAsync();
        Assert.Contains("legacy", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaintextLegacyBlob_StillReadable()
    {
        var root = Path.Combine(Path.GetTempPath(), "chatapp-export-tests", Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new DataExportStorageOptions
        {
            LocalRootPath = root,
            EncryptAtRest = false,
        });
        var blob = CreateBlobStore(opts);
        var objectKey = "u1/plain.json";
        await using (var ms = new MemoryStream("{\"ok\":1}"u8.ToArray()))
            await blob.WriteAsync(objectKey, ms);

        await using var stream = await blob.OpenReadAsync(objectKey);
        using var reader = new StreamReader(stream!);
        Assert.Contains("ok", await reader.ReadToEndAsync(), StringComparison.Ordinal);
    }

    private sealed class FailingDeleteBlobStore(IDataExportBlobStore inner) : IDataExportBlobStore
    {
        public Task WriteAsync(string objectKey, Stream content, CancellationToken cancellationToken = default)
            => inner.WriteAsync(objectKey, content, cancellationToken);

        public Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
            => inner.OpenReadAsync(objectKey, cancellationToken);

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
            => throw new IOException("simulated blob delete failure");
    }

    private static LocalDataExportBlobStore CreateBlobStore(IOptions<DataExportStorageOptions> opts)
        => new(
            opts,
            Options.Create(new SecurityOptions { SecretEncryptionKey = "test-mfa-encryption-key", KeyVersion = 1 }),
            Options.Create(new JwtSettings { Secret = "test-mfa-jwt-secret-please-change" }),
            new DummyHost(),
            NullLogger<LocalDataExportBlobStore>.Instance);

    private TrustedDeviceService CreateTrusted(UserDbContext db)
    {
        var security = new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance);
        var hasher = AuthTestFactories.CreatePasswordHasher();
        var mfa = new MfaService(
            db, hasher,
            CreateRecoveryHasher(),
            new AesGcmMfaSecretProtector(
                Options.Create(new SecurityOptions { SecretEncryptionKey = "test-mfa-encryption-key", KeyVersion = 1 }),
                Options.Create(new JwtSettings { Secret = "test-mfa-jwt-secret-please-change" }),
                new DummyHost(),
                NullLogger<AesGcmMfaSecretProtector>.Instance),
            security,
            redis.Cache,
            NullLogger<MfaService>.Instance);
        return new TrustedDeviceService(
            db,
            security,
            hasher,
            mfa,
            redis.Cache,
            new FixedDeviceInfo(AuthTestFactories.StableDeviceId("export-test")),
            new Microsoft.AspNetCore.Http.HttpContextAccessor(),
            Options.Create(new TrustedDeviceOptions()),
            NullLogger<TrustedDeviceService>.Instance);
    }

    private TokenService CreateTokenSessions()
    {
        var jwt = Options.Create(new JwtSettings
        {
            AccessTokenExpirationMinutes = 30,
            RefreshTokenLength = 32,
            RefreshTokenExpirationDays = 3,
            Issuer = "ChatApp",
            Audience = "ChatApp",
        });
        return new TokenService(
            redis.Cache,
            new FixedDeviceInfo(AuthTestFactories.StableDeviceId("export-test")),
            jwt,
            NullLogger<TokenService>.Instance);
    }

    private static async Task<ApplicationUser> SeedAsync(UserDbContext db, string password)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var hasher = AuthTestFactories.CreatePasswordHasher();
        var user = new ApplicationUser
        {
            Id = new TsidGeneratorService().GenerateTsid(),
            UserName = $"ex-{suffix}",
            NormalizedUserName = $"EX-{suffix}",
            Email = $"ex-{suffix}@ex.com",
            NormalizedEmail = $"EX-{suffix}@EX.COM",
            EmailConfirmed = true,
            PasswordHash = await hasher.HashPasswordAsync(password),
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static IRecoveryCodeHasher CreateRecoveryHasher()
        => new HmacRecoveryCodeHasher(
            Options.Create(new SecurityOptions { SecretEncryptionKey = "test-mfa-encryption-key", KeyVersion = 1 }),
            Options.Create(new JwtSettings { Secret = "test-mfa-jwt-secret-please-change" }),
            new DummyHost(),
            AuthTestFactories.CreateCpuLimiter(),
            NullLogger<HmacRecoveryCodeHasher>.Instance);

    private sealed class ExportTestScopeFactory(
        PostgresTestFixture postgres,
        TokenService sessions,
        IDataExportBlobStore blob,
        IOptions<DataExportStorageOptions> opts) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            var services = new ServiceCollection();
            services.AddSingleton(opts);
            services.AddSingleton(blob);
            services.AddSingleton<ISessionStore>(sessions);
            services.AddSingleton<IRealtimeChatExportReader>(new FakeChatExportReader());
            services.AddSingleton<IAttachmentMetadataStore>(UnavailableAttachmentMetadataStore.Instance);
            services.AddScoped(_ => postgres.CreateContext());
            return services.BuildServiceProvider().CreateScope();
        }
    }

    private sealed class FakeChatExportReader : IRealtimeChatExportReader
    {
        public bool IsAvailable => true;
        public string UnavailableReason => string.Empty;

        public Task<ChatExportPage> ReadPageAsync(
            long userId,
            long? beforeReceivedAtMs,
            string? beforeMessageId,
            int take,
            CancellationToken cancellationToken = default)
        {
            if (beforeReceivedAtMs is not null)
                return Task.FromResult(new ChatExportPage([], false, null, null));

            var msg = CreateMessage(userId);
            return Task.FromResult(new ChatExportPage([msg], false, null, null));
        }

        public async IAsyncEnumerable<ChatExportMessage> ReadMessagesAsync(
            long userId,
            int maxMessages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            if (maxMessages <= 0)
                yield break;
            yield return CreateMessage(userId);
        }

        private static ChatExportMessage CreateMessage(long userId) => new(
            MessageId: "m-export-1",
            ClientMessageId: "c-1",
            SenderUserId: userId,
            ReceiverUserId: userId + 1,
            Content: """{"attachments":[{"url":"https://cdn.example/a.png","name":"a.png","mime":"image/png"}]}""",
            ReceivedAtMs: 1_700_000_000_000,
            DeliveredAtMs: 1_700_000_000_100,
            ReadAtMs: 1_700_000_000_200);
    }

    private sealed class DummyHost : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
