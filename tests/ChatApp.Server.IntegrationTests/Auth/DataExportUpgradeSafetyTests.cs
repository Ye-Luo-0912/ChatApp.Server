using ChatApp.Server.IntegrationTests.Support;
using Core.Models.Export;
using Infrastructure.Data;
using Infrastructure.Services.Utilities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

/// <summary>
/// 模拟从「仅有 DataExportJob、尚无 UX_DataExportJob_OneActive」升级：
/// 先写入重复活跃作业，再执行与 Harden 迁移相同的去重 SQL，最后确保唯一索引可创建。
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DataExportUpgradeSafetyTests(PostgresTestFixture postgres)
{
    private const string DedupeSql = """
        WITH ranked AS (
            SELECT "Id",
                   ROW_NUMBER() OVER (
                       PARTITION BY "UserId"
                       ORDER BY "CreatedAt" DESC, "Id" DESC
                   ) AS rn
            FROM "T_DataExportJob"
            WHERE "ConsumedAt" IS NULL
              AND "Status" IN ('Pending', 'Processing', 'Ready')
        )
        UPDATE "T_DataExportJob" AS j
        SET "Status" = 'Failed',
            "Error" = 'superseded_duplicate_active',
            "LeaseOwner" = NULL,
            "LeaseUntil" = NULL
        FROM ranked AS r
        WHERE j."Id" = r."Id" AND r.rn > 1;
        """;

    [SkippableFact]
    public async Task Upgrade_DedupesDuplicateActiveJobs_ThenUniqueIndexHolds()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var userId = new TsidGeneratorService().GenerateTsid();
        var older = Guid.NewGuid().ToString("N");
        var newer = Guid.NewGuid().ToString("N");

        // 临时去掉唯一索引以模拟升级前状态。
        await db.Database.ExecuteSqlRawAsync(
            """DROP INDEX IF EXISTS "UX_DataExportJob_OneActive";""");

        try
        {
            db.DataExportJobs.AddRange(
                new DataExportJob
                {
                    Id = older,
                    UserId = userId,
                    Status = DataExportJobStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                },
                new DataExportJob
                {
                    Id = newer,
                    UserId = userId,
                    Status = DataExportJobStatus.Ready,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ReadyAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    ObjectKey = $"{userId}/newer.json",
                });
            await db.SaveChangesAsync();

            Assert.Equal(2, await db.DataExportJobs.CountAsync(j =>
                j.UserId == userId
                && j.ConsumedAt == null
                && (j.Status == DataExportJobStatus.Pending
                    || j.Status == DataExportJobStatus.Processing
                    || j.Status == DataExportJobStatus.Ready)));

            await db.Database.ExecuteSqlRawAsync(DedupeSql);
            db.ChangeTracker.Clear();

            var active = await db.DataExportJobs.AsNoTracking()
                .Where(j => j.UserId == userId
                            && j.ConsumedAt == null
                            && (j.Status == DataExportJobStatus.Pending
                                || j.Status == DataExportJobStatus.Processing
                                || j.Status == DataExportJobStatus.Ready))
                .ToListAsync();
            Assert.Single(active);
            Assert.Equal(newer, active[0].Id);

            var superseded = await db.DataExportJobs.AsNoTracking().SingleAsync(j => j.Id == older);
            Assert.Equal(DataExportJobStatus.Failed, superseded.Status);
            Assert.Equal("superseded_duplicate_active", superseded.Error);

            await db.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS "UX_DataExportJob_OneActive"
                ON "T_DataExportJob" ("UserId")
                WHERE "ConsumedAt" IS NULL AND "Status" IN ('Pending', 'Processing', 'Ready');
                """);

            // 唯一索引生效：再插入第二个活跃作业应失败。
            await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
            {
                db.DataExportJobs.Add(new DataExportJob
                {
                    Id = Guid.NewGuid().ToString("N"),
                    UserId = userId,
                    Status = DataExportJobStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            });
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                """DROP INDEX IF EXISTS "UX_DataExportJob_OneActive";""");
            await db.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS "UX_DataExportJob_OneActive"
                ON "T_DataExportJob" ("UserId")
                WHERE "ConsumedAt" IS NULL AND "Status" IN ('Pending', 'Processing', 'Ready');
                """);
            await db.DataExportJobs.Where(j => j.UserId == userId).ExecuteDeleteAsync();
        }
    }
}
