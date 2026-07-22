using ChatApp.Realtime.Abstractions.Events;
using Core.Models.Export;
using Infrastructure.Data;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

/// <summary>
/// 不依赖 Testcontainers：验证 Saga 完成 / 幂等 / mismatch / 乱序 / DLQ / 重放。
/// </summary>
public sealed class AccountCleanupSagaServiceTests
{
    [Fact]
    public async Task TryComplete_MarksCompleted_AndDuplicateIsIdempotent()
    {
        await using var db = CreateDb();
        const long userId = 42;
        const string sourceEventId = "src-event-1";
        db.AccountCleanupSagas.Add(new AccountCleanupSaga
        {
            UserId = userId,
            EventId = sourceEventId,
            Status = AccountCleanupSagaStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var svc = new AccountCleanupSagaService(db, NullLogger<AccountCleanupSagaService>.Instance);
        var completedId = $"cleanup-done:{sourceEventId}";

        Assert.Equal(AccountCleanupApplyResult.Completed, await svc.TryCompleteAsync(userId, completedId));
        Assert.Equal(AccountCleanupApplyResult.DuplicateDelivery, await svc.TryCompleteAsync(userId, completedId));

        var saga = await db.AccountCleanupSagas.AsNoTracking().SingleAsync(s => s.UserId == userId);
        Assert.Equal(AccountCleanupSagaStatus.Completed, saga.Status);
        Assert.NotNull(saga.CompletedAt);
        Assert.Null(saga.LastError);
        Assert.True(await db.AccountCleanupInbox.AnyAsync(x => x.EventId == completedId));
    }

    [Fact]
    public async Task TryComplete_Mismatch_DoesNotComplete()
    {
        await using var db = CreateDb();
        const long userId = 55;
        db.AccountCleanupSagas.Add(new AccountCleanupSaga
        {
            UserId = userId,
            EventId = "expected-event",
            Status = AccountCleanupSagaStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var svc = new AccountCleanupSagaService(db, NullLogger<AccountCleanupSagaService>.Instance);
        var result = await svc.TryCompleteAsync(userId, "cleanup-done:other-event");
        Assert.Equal(AccountCleanupApplyResult.EventIdMismatch, result);

        var saga = await db.AccountCleanupSagas.AsNoTracking().SingleAsync(s => s.UserId == userId);
        Assert.Equal(AccountCleanupSagaStatus.Pending, saga.Status);
        Assert.Null(saga.CompletedAt);
    }

    [Fact]
    public async Task TryComplete_MissingSaga_ReturnsMissing()
    {
        await using var db = CreateDb();
        var svc = new AccountCleanupSagaService(db, NullLogger<AccountCleanupSagaService>.Instance);
        Assert.Equal(
            AccountCleanupApplyResult.MissingSaga,
            await svc.TryCompleteAsync(999, "cleanup-done:no-saga"));
    }

    [Fact]
    public async Task TryComplete_InvalidPrefix_Rejected()
    {
        await using var db = CreateDb();
        db.AccountCleanupSagas.Add(new AccountCleanupSaga
        {
            UserId = 3,
            EventId = "e3",
            Status = AccountCleanupSagaStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var svc = new AccountCleanupSagaService(db, NullLogger<AccountCleanupSagaService>.Instance);
        Assert.Equal(
            AccountCleanupApplyResult.InvalidCompletedEventId,
            await svc.TryCompleteAsync(3, "e3"));

        var saga = await db.AccountCleanupSagas.AsNoTracking().SingleAsync(s => s.UserId == 3);
        Assert.Equal(AccountCleanupSagaStatus.Pending, saga.Status);
    }

    [Fact]
    public async Task RecordDeadLetter_PersistsAndInboxDedupes()
    {
        await using var db = CreateDb();
        var svc = new AccountCleanupSagaService(db, NullLogger<AccountCleanupSagaService>.Instance);
        var evt = new RealtimeEvent
        {
            EventId = "cleanup-done:dead-1",
            Type = RealtimeEventType.AccountCleanupCompleted,
            TargetUserId = 11,
            OccurredAtMs = 1,
        };

        await svc.RecordDeadLetterAsync(
            evt.EventId,
            evt.TargetUserId,
            evt.PayloadJson,
            AccountCleanupDeadLetterReason.EventIdMismatch,
            "mismatch",
            3);
        await svc.RecordDeadLetterAsync(
            evt.EventId,
            evt.TargetUserId,
            evt.PayloadJson,
            AccountCleanupDeadLetterReason.EventIdMismatch,
            "mismatch-again",
            4);

        Assert.Equal(1, await db.AccountCleanupDeadLetters.CountAsync());
        Assert.Equal(1, await db.AccountCleanupInbox.CountAsync(x => x.EventId == evt.EventId));
        Assert.Equal(
            AccountCleanupApplyResult.DuplicateDelivery,
            await svc.TryCompleteAsync(11, evt.EventId));
    }

    [Fact]
    public async Task TryReplay_FailedSaga_RepublishesOutbox()
    {
        await using var db = CreateDb();
        const long userId = 77;
        const string eventId = "replay-evt";
        db.AccountCleanupSagas.Add(new AccountCleanupSaga
        {
            UserId = userId,
            EventId = eventId,
            Status = AccountCleanupSagaStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            CompletedAt = DateTimeOffset.UtcNow.AddHours(-1),
            LastError = "pending_timeout",
        });
        await db.SaveChangesAsync();

        var svc = new AccountCleanupSagaService(db, NullLogger<AccountCleanupSagaService>.Instance);
        Assert.True(await svc.TryReplayAsync(userId));

        var saga = await db.AccountCleanupSagas.AsNoTracking().SingleAsync(s => s.UserId == userId);
        Assert.Equal(AccountCleanupSagaStatus.Pending, saga.Status);
        Assert.Null(saga.LastError);
        Assert.True(await db.RealtimeOutbox.AnyAsync(o => o.EventId == eventId));
    }

    [Fact]
    public async Task TryApplyCompletedEvent_IgnoresNonCompletedTypes()
    {
        await using var db = CreateDb();
        db.AccountCleanupSagas.Add(new AccountCleanupSaga
        {
            UserId = 7,
            EventId = "e7",
            Status = AccountCleanupSagaStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var svc = new AccountCleanupSagaService(db, NullLogger<AccountCleanupSagaService>.Instance);
        Assert.Equal(
            AccountCleanupApplyResult.InvalidCompletedEventId,
            await svc.TryApplyCompletedEventAsync(new RealtimeEvent
            {
                EventId = "e7",
                Type = RealtimeEventType.UserAccountDeleted,
                TargetUserId = 7,
                OccurredAtMs = 1,
            }));

        var saga = await db.AccountCleanupSagas.AsNoTracking().SingleAsync(s => s.UserId == 7);
        Assert.Equal(AccountCleanupSagaStatus.Pending, saga.Status);
    }

    [Fact]
    public async Task FailStalePending_MarksFailed()
    {
        await using var db = CreateDb();
        db.AccountCleanupSagas.Add(new AccountCleanupSaga
        {
            UserId = 99,
            EventId = "old",
            Status = AccountCleanupSagaStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-5),
        });
        await db.SaveChangesAsync();

        var svc = new AccountCleanupSagaService(db, NullLogger<AccountCleanupSagaService>.Instance);
        Assert.Equal(1, await svc.FailStalePendingAsync(TimeSpan.FromHours(72)));

        var saga = await db.AccountCleanupSagas.AsNoTracking().SingleAsync(s => s.UserId == 99);
        Assert.Equal(AccountCleanupSagaStatus.Failed, saga.Status);
        Assert.Equal("pending_timeout", saga.LastError);
    }

    [Fact]
    public void TryGetSourceEventId_StripsPrefix()
    {
        Assert.Equal("abc", AccountCleanupSagaService.TryGetSourceEventId("cleanup-done:abc"));
        Assert.Null(AccountCleanupSagaService.TryGetSourceEventId("abc"));
    }

    private static UserDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new UserDbContext(options);
    }
}
