using ChatApp.Server.Controllers;
using Core.Settings;
using Infrastructure.Services;
using Infrastructure.Validation;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Quality;

public sealed class StrictModeContractTests
{
    [Fact]
    public async Task DataExportStagingBudget_AdmitsOnlyWithinConcurrentByteLimit()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "chatapp-export-budget-test",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var budget = new DataExportStagingBudget(
                Options.Create(new DataExportStorageOptions
                {
                    LocalRootPath = root,
                    StagingMaxBytes = 10,
                }),
                NullLogger<DataExportStagingBudget>.Instance);

            await using var first = await budget.ReserveAsync(7);
            var waiting = budget.ReserveAsync(4).AsTask();
            await Task.Delay(25);
            Assert.False(waiting.IsCompleted);

            await first.DisposeAsync();
            await using var second = await waiting;
            Assert.Equal(4, budget.CurrentReservedBytes);
            await second.DisposeAsync();
            Assert.Equal(0, budget.CurrentReservedBytes);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DataExportStagingReservation_CoversFinalAndThreeSideFiles()
    {
        var options = new DataExportStorageOptions
        {
            MaxExportBytes = 100,
            StagingMaxBytes = 400,
        };

        Assert.Equal(
            400,
            DataExportJobProcessor.GetStagingReservationBytes(options));

        var invalid = new DataExportStorageOptions
        {
            MaxExportBytes = 100,
            StagingMaxBytes = 399,
        };
        var result = new DataExportStorageOptionsValidator(new TestHostEnvironment())
            .Validate(null, invalid);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, x => x.Contains("4 倍", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScanStagingBudget_BlocksAboveByteBudget_AndReleasesExactlyOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "chatapp-staging-test", Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new AttachmentStorageOptions
            {
                MaxBytes = 10,
                ScanMaxConcurrentBytes = 10,
                ScanStagingMaxBytes = 10,
                TmpfsSizeBytes = 10,
                ScanStagingRoot = root,
            });
            using var budget = new AttachmentScanStagingBudget(
                options,
                NullLogger<AttachmentScanStagingBudget>.Instance);

            await using var first = await budget.ReserveAsync(10);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => budget.ReserveAsync(1, timeout.Token).AsTask());

            await first.DisposeAsync();
            await using var second = await budget.ReserveAsync(10);
            Assert.Equal(10, budget.CurrentBytes);
            await second.DisposeAsync();
            Assert.Equal(0, budget.CurrentBytes);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StreamingEndpoints_DisableGlobalRequestTimeout()
    {
        Assert.NotNull(typeof(AttachmentsController)
            .GetMethod(nameof(AttachmentsController.Download))
            ?.GetCustomAttributes(typeof(DisableRequestTimeoutAttribute), inherit: true)
            .SingleOrDefault());
        Assert.NotNull(typeof(UsersController)
            .GetMethod(nameof(UsersController.DownloadExportJob))
            ?.GetCustomAttributes(typeof(DisableRequestTimeoutAttribute), inherit: true)
            .SingleOrDefault());
    }

    [Fact]
    public void HealthDependencyDefaults_RequireWorkerRealtimeOnlyWhenFeatureIsEnabled()
    {
        var api = new HealthDependencyOptions { ProcessRole = "Api" };
        var worker = new HealthDependencyOptions { ProcessRole = "Worker" };

        Assert.False(api.RequireRealtimeOutbox);
        Assert.True(worker.RequireRealtimeOutbox);
        Assert.False(api.RequireAttachmentMetadata(configured: false));
        Assert.True(api.RequireAttachmentMetadata(configured: true));
    }

    [Fact]
    public void DatabasePoolTimeouts_AreRoleSpecific_AndMigrationIsIndependent()
    {
        var api = new DatabasePoolOptions { Role = "Api" };
        var worker = new DatabasePoolOptions { Role = "Worker" };
        var all = new DatabasePoolOptions { Role = "All" };
        var migration = new DatabasePoolOptions { Role = "Api", UseMigrationTimeout = true };

        Assert.Equal(5, api.EffectiveCommandTimeoutSeconds);
        Assert.Equal(120, worker.EffectiveCommandTimeoutSeconds);
        Assert.Equal(15, all.EffectiveCommandTimeoutSeconds);
        Assert.Equal(120, migration.EffectiveCommandTimeoutSeconds);
    }

    [Fact]
    public void ApiPerformanceLoginRiskCanBeExplicitlyDisabledWithoutChangingDefault()
    {
        Assert.True(new LoginRiskOptions().Enabled);
        var performance = new LoginRiskOptions { Enabled = false };

        Assert.False(performance.Enabled);
        DisabledLoginRiskAnalyzer.Instance.Enqueue(new Core.Interfaces.LoginRiskWorkItem(
            UserId: 1,
            ClientIp: null,
            DeviceId: null,
            IsNewDevice: false,
            SessionId: null));
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
