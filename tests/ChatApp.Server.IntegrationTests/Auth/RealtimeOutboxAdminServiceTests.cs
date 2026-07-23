using ChatApp.Realtime.Abstractions.Events;
using ChatApp.Realtime.Abstractions.Stores;
using ChatApp.Realtime.Integration.Outbox;
using ChatApp.Server.IntegrationTests.Support;
using Infrastructure.Services;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisPostgresCollection))]
public sealed class RealtimeOutboxAdminServiceTests(PostgresTestFixture postgres)
{
    [SkippableFact]
    public async Task Summary_List_And_SafeReplay()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        await using var db = postgres.CreateContext();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        db.RealtimeOutbox.AddRange(
            new RealtimeIntegrationOutboxItem
            {
                EventId = "ops-pending-1",
                PayloadJson = """{"type":5}""",
                TargetUserId = 42,
                EventType = (short)RealtimeEventType.MessageReceived,
                Status = (short)RealtimeOutboxStatus.Pending,
                CreatedAtMs = now - 60_000,
                NextAttemptAtMs = now,
                AttemptCount = 3,
                LastError = "temp",
            },
            new RealtimeIntegrationOutboxItem
            {
                EventId = "ops-dead-1",
                PayloadJson = """{"type":8}""",
                TargetUserId = 42,
                EventType = (short)RealtimeEventType.UserAccountDeleted,
                Status = (short)RealtimeOutboxStatus.Dead,
                CreatedAtMs = now - 120_000,
                NextAttemptAtMs = now,
                AttemptCount = 10,
                LastError = "poison",
            },
            new RealtimeIntegrationOutboxItem
            {
                EventId = "ops-published-1",
                PayloadJson = """{"type":5}""",
                TargetUserId = 7,
                EventType = (short)RealtimeEventType.MessageReceived,
                Status = (short)RealtimeOutboxStatus.Published,
                CreatedAtMs = now - 10_000,
                NextAttemptAtMs = now,
                PublishedAtMs = now - 5_000,
                AttemptCount = 1,
            });
        await db.SaveChangesAsync();

        var svc = new RealtimeOutboxAdminService(db);
        var summary = await svc.GetSummaryAsync();
        Assert.True(summary.PendingCount >= 1);
        Assert.True(summary.DeadCount >= 1);
        Assert.True(summary.OldestPendingAgeMs is > 0);

        var dead = await svc.ListAsync(status: "Dead", targetUserId: 42, offset: 0, limit: 20);
        Assert.Contains(dead.Items, x => x.EventId == "ops-dead-1" && x.LastError == "poison");

        var (pubOk, pubErr) = await svc.ReplayDeadAsync("ops-published-1");
        Assert.False(pubOk);
        Assert.Equal("already_published", pubErr);

        var (pendingOk, pendingErr) = await svc.ReplayDeadAsync("ops-pending-1");
        Assert.False(pendingOk);
        Assert.Equal("not_dead", pendingErr);

        var (ok, err) = await svc.ReplayDeadAsync("ops-dead-1");
        Assert.True(ok);
        Assert.Null(err);

        db.ChangeTracker.Clear();
        var replayed = await db.RealtimeOutbox.FindAsync("ops-dead-1");
        Assert.NotNull(replayed);
        Assert.Equal((short)RealtimeOutboxStatus.Pending, replayed!.Status);
        Assert.Equal(0, replayed.AttemptCount);
        Assert.Null(replayed.LastError);
    }

    [SkippableFact]
    public async Task ReplayDeadBatch_SingleRoundTrip_ReplaysOnlyDead()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        await using var db = postgres.CreateContext();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var dead1 = $"batch-dead-1-{suffix}";
        var dead2 = $"batch-dead-2-{suffix}";
        var pending = $"batch-pending-{suffix}";
        var published = $"batch-pub-{suffix}";
        var missing = $"batch-missing-{suffix}";

        db.RealtimeOutbox.AddRange(
            new RealtimeIntegrationOutboxItem
            {
                EventId = dead1,
                PayloadJson = """{"type":5}""",
                TargetUserId = 1,
                EventType = (short)RealtimeEventType.MessageReceived,
                Status = (short)RealtimeOutboxStatus.Dead,
                CreatedAtMs = now,
                NextAttemptAtMs = now,
                AttemptCount = 9,
                LastError = "x",
            },
            new RealtimeIntegrationOutboxItem
            {
                EventId = dead2,
                PayloadJson = """{"type":5}""",
                TargetUserId = 1,
                EventType = (short)RealtimeEventType.MessageReceived,
                Status = (short)RealtimeOutboxStatus.Dead,
                CreatedAtMs = now,
                NextAttemptAtMs = now,
                AttemptCount = 9,
            },
            new RealtimeIntegrationOutboxItem
            {
                EventId = pending,
                PayloadJson = """{"type":5}""",
                TargetUserId = 1,
                EventType = (short)RealtimeEventType.MessageReceived,
                Status = (short)RealtimeOutboxStatus.Pending,
                CreatedAtMs = now,
                NextAttemptAtMs = now,
            },
            new RealtimeIntegrationOutboxItem
            {
                EventId = published,
                PayloadJson = """{"type":5}""",
                TargetUserId = 1,
                EventType = (short)RealtimeEventType.MessageReceived,
                Status = (short)RealtimeOutboxStatus.Published,
                CreatedAtMs = now,
                NextAttemptAtMs = now,
                PublishedAtMs = now,
            });
        await db.SaveChangesAsync();

        var svc = new RealtimeOutboxAdminService(db);
        var result = await svc.ReplayDeadBatchAsync([dead1, dead2, pending, published, missing]);

        Assert.Equal(5, result.Requested);
        Assert.Equal(2, result.Replayed);
        Assert.Contains(result.Skipped, s => s.StartsWith($"{pending}:", StringComparison.Ordinal));
        Assert.Contains(result.Skipped, s => s.StartsWith($"{published}:already_published", StringComparison.Ordinal));
        Assert.Contains(result.Skipped, s => s.StartsWith($"{missing}:not_found", StringComparison.Ordinal));

        db.ChangeTracker.Clear();
        Assert.Equal((short)RealtimeOutboxStatus.Pending, (await db.RealtimeOutbox.FindAsync(dead1))!.Status);
        Assert.Equal((short)RealtimeOutboxStatus.Pending, (await db.RealtimeOutbox.FindAsync(dead2))!.Status);
        Assert.Equal((short)RealtimeOutboxStatus.Pending, (await db.RealtimeOutbox.FindAsync(pending))!.Status);
        Assert.Equal((short)RealtimeOutboxStatus.Published, (await db.RealtimeOutbox.FindAsync(published))!.Status);
    }
}
