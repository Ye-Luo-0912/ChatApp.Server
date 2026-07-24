using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using ChatApp.Server.IntegrationTests.Support;
using Infrastructure.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisPostgresCollection))]
public sealed class AttachmentOpsAdminServiceTests(PostgresTestFixture postgres)
{
    [SkippableFact]
    public async Task DeleteFailures_And_ScanBacklog_Aggregate()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        await using var db = postgres.CreateContext();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        db.AttachmentBlobDeleteJobs.AddRange(
            new AttachmentBlobDeleteJob
            {
                ObjectKey = $"attachments/ops-del-high-{suffix}.bin",
                AttachmentId = $"att-del-high-{suffix}",
                UserId = 1,
                Status = AttachmentBlobDeleteJobStatus.Pending,
                AttemptCount = 8,
                NextAttemptAt = now,
                CreatedAt = now.AddHours(-2),
                LastError = "s3_timeout",
            },
            new AttachmentBlobDeleteJob
            {
                ObjectKey = $"attachments/ops-del-done-{suffix}.bin",
                Status = AttachmentBlobDeleteJobStatus.Done,
                AttemptCount = 1,
                NextAttemptAt = now,
                CreatedAt = now.AddHours(-1),
                CompletedAt = now,
            });
        db.AttachmentScanJobs.AddRange(
            new AttachmentScanJob
            {
                AttachmentId = $"att-scan-pending-{suffix}",
                ObjectKey = $"attachments/ops-scan-{suffix}.bin",
                UserId = 2,
                Status = AttachmentScanJobStatus.Pending,
                AttemptCount = 3,
                NextAttemptAt = now.AddMinutes(5),
                CreatedAt = now.AddHours(-3),
                LastError = "scanner_busy",
                SizeBytes = 12,
            },
            new AttachmentScanJob
            {
                AttachmentId = $"att-scan-dead-{suffix}",
                ObjectKey = $"attachments/ops-scan-dead-{suffix}.bin",
                UserId = 2,
                Status = AttachmentScanJobStatus.DeadLetter,
                AttemptCount = 10,
                NextAttemptAt = now,
                CreatedAt = now.AddHours(-4),
                LastError = "exhausted",
                SizeBytes = 8,
            });
        await db.SaveChangesAsync();

        var opts = Options.Create(new AttachmentStorageOptions
        {
            OpsHighDeleteAttemptThreshold = 5,
            OpsSampleLimit = 20,
            MaxScanAttempts = 10,
            StuckScanningMinutes = 30,
            TicketMinutes = 15,
        });
        var svc = new AttachmentOpsAdminService(db, UnavailableAttachmentMetadataStore.Instance, opts);

        var deletes = await svc.GetDeleteFailuresAsync();
        Assert.True(deletes.PendingCount >= 1);
        Assert.True(deletes.HighAttemptPendingCount >= 1);
        Assert.Contains(deletes.WorstPending, x => x.LastError == "s3_timeout" && x.AttemptCount >= 8);

        var scans = await svc.GetScanBacklogAsync();
        Assert.True(scans.PendingCount >= 1);
        Assert.True(scans.DeadLetterCount >= 1);
        Assert.True(scans.RetryingCount >= 1);
        Assert.True(scans.ExhaustedLikeCount >= 1);
        Assert.Contains(scans.WorstOpen, x => x.AttachmentId == $"att-scan-dead-{suffix}");

        var orphans = await svc.GetOrphansAsync();
        Assert.False(orphans.MetadataAvailable);

        var hints = await svc.GetHintsAsync();
        Assert.False(hints.MetadataAvailable);
        Assert.Contains("attachment.scan", hints.RelatedMetricNames);
    }
}
