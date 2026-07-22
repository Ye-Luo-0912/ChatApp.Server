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
        var export = new DataExportService(db, trusted, blob, opts, new ExportTestScopeFactory(postgres, CreateTokenSessions(), blob, opts));

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
        Assert.Equal("CAE1"u8.ToArray(), onDisk.AsSpan(0, 4).ToArray());
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
        }

        var (again, _, err2) = await export.OpenDownloadAsync(user.Id, jobId!, CancellationToken.None);
        Assert.Null(again);
        Assert.Equal("下载链接已使用", err2);
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
        var export = new DataExportService(db, trusted, blob, opts, new ExportTestScopeFactory(postgres, CreateTokenSessions(), blob, opts));

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
        Assert.Equal("导出文件缺失", err);

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
        var export = new DataExportService(db, trusted, failing, opts, scopeFactory);

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
        var hasher = new BcryptPasswordHasher();
        var mfa = new MfaService(
            db, hasher,
            CreateRecoveryHasher(),
            new AesGcmMfaSecretProtector(
                Options.Create(new SecurityOptions { SecretEncryptionKey = "test-mfa-encryption-key", KeyVersion = 1 }),
                Options.Create(new JwtSettings { Secret = "test-mfa-jwt-secret-please-change" }),
                new DummyHost(),
                NullLogger<AesGcmMfaSecretProtector>.Instance),
            security,
            NullLogger<MfaService>.Instance);
        return new TrustedDeviceService(
            db, security, hasher, mfa, redis.Cache, NullLogger<TrustedDeviceService>.Instance);
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
            new FixedDeviceInfo("export-test"),
            jwt,
            NullLogger<TokenService>.Instance);
    }

    private static async Task<ApplicationUser> SeedAsync(UserDbContext db, string password)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var hasher = new BcryptPasswordHasher();
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
            services.AddScoped(_ => postgres.CreateContext());
            return services.BuildServiceProvider().CreateScope();
        }
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
