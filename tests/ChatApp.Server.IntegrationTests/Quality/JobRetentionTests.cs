using ChatApp.Server.IntegrationTests.Support;
using Core.Models.Export;
using Core.Models.Security;
using Core.Settings;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Quality;

[Collection(nameof(PostgresCollection))]
public sealed class JobRetentionTests(PostgresTestFixture postgres)
{
    [SkippableFact]
    public async Task AttachmentScanAudit_IsPartitionedForBoundedColdHistory()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var partitioned = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1
                FROM pg_partitioned_table p
                JOIN pg_class c ON c.oid = p.partrelid
                WHERE c.relname = 'T_AttachmentScanAudit') AS "Value"
            """).SingleAsync();

        Assert.True(partitioned);
    }

    [SkippableFact]
    public async Task Purge_RemovesOldConfirmSagasAndDeadLettersWithoutCompletedAt()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        var old = DateTimeOffset.UtcNow.AddDays(-10);
        await using var db = postgres.CreateContext();
        var risk = new LoginRiskOutboxItem
        {
            UserId = 1,
            Status = LoginRiskOutboxStatus.DeadLetter,
            AttemptCount = 8,
            NextAttemptAt = old,
            CreatedAt = old,
            UpdatedAt = old,
            LastError = "exhausted",
            CompletedAt = null,
        };
        var saga = new AttachmentConfirmSaga
        {
            AttachmentId = $"retention-{Guid.NewGuid():N}",
            UserId = 1,
            ObjectKey = "pending/retention-test",
            Status = AttachmentConfirmSagaStatus.Completed,
            CreatedAt = old,
            UpdatedAt = old,
            CompletedAt = old,
        };
        db.LoginRiskOutbox.Add(risk);
        db.AttachmentConfirmSagas.Add(saga);
        await db.SaveChangesAsync();

        var policy = Options.Create(new JobRetentionPolicy
        {
            BatchSize = 100,
            PollIntervalSeconds = 30,
            LoginRiskRetentionDays = 1,
            AttachmentConfirmSagaRetentionDays = 1,
            ScanJobRetentionDays = 1,
            ScanProjectionRetentionDays = 1,
            ScanAuditRetentionDays = 1,
            AttachmentBlobDeleteRetentionDays = 1,
            LoginAuditRetentionDays = 1,
        });
        var service = new JobRetentionService(
            db,
            policy,
            NullLogger<JobRetentionService>.Instance);

        var result = await service.PurgeAsync();

        Assert.True(result.LoginRiskOutbox >= 1);
        Assert.True(result.AttachmentConfirmSagas >= 1);
        db.ChangeTracker.Clear();
        Assert.False(await db.LoginRiskOutbox.AnyAsync(x => x.Id == risk.Id));
        Assert.False(await db.AttachmentConfirmSagas.AnyAsync(x => x.Id == saga.Id));
    }
}
