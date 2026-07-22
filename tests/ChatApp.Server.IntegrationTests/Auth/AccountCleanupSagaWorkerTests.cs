using ChatApp.Realtime.Abstractions.Events;
using ChatApp.Realtime.Integration;
using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Data;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

/// <summary>AccountCleanupSagaWorker ACK/NAK/DLQ 决策（无 NATS）。</summary>
public sealed class AccountCleanupSagaWorkerTests
{
    [Fact]
    public async Task Handle_Mismatch_WritesDlqAndAcks()
    {
        await using var db = CreateDb();
        db.AccountCleanupSagas.Add(new AccountCleanupSaga
        {
            UserId = 1,
            EventId = "real",
            Status = AccountCleanupSagaStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var (worker, acked, naked) = CreateWorker(db);
        var delivery = CreateDelivery(
            new RealtimeEvent
            {
                EventId = "cleanup-done:wrong",
                Type = RealtimeEventType.AccountCleanupCompleted,
                TargetUserId = 1,
                OccurredAtMs = 1,
            },
            deliveryCount: 1,
            acked,
            naked);

        await InvokeHandleAsync(worker, delivery);

        Assert.Single(acked);
        Assert.Empty(naked);
        Assert.Equal(AccountCleanupSagaStatus.Pending,
            (await db.AccountCleanupSagas.SingleAsync()).Status);
        Assert.Equal(1, await db.AccountCleanupDeadLetters.CountAsync());
    }

    [Fact]
    public async Task Handle_MissingSaga_NaksUntilExhaustedThenDlq()
    {
        await using var db = CreateDb();
        var (worker, acked, naked) = CreateWorker(db, maxMissing: 3);

        var evt = new RealtimeEvent
        {
            EventId = "cleanup-done:early",
            Type = RealtimeEventType.AccountCleanupCompleted,
            TargetUserId = 88,
            OccurredAtMs = 1,
        };

        await InvokeHandleAsync(worker, CreateDelivery(evt, 1, acked, naked));
        Assert.Empty(acked);
        Assert.Single(naked);

        await InvokeHandleAsync(worker, CreateDelivery(evt, 2, acked, naked));
        Assert.Empty(acked);
        Assert.Equal(2, naked.Count);

        await InvokeHandleAsync(worker, CreateDelivery(evt, 3, acked, naked));
        Assert.Single(acked);
        Assert.Equal(2, naked.Count);
        Assert.Equal(1, await db.AccountCleanupDeadLetters.CountAsync(x =>
            x.ReasonCode == AccountCleanupDeadLetterReason.MissingSagaExhausted));
    }

    [Fact]
    public async Task Handle_DuplicateDelivery_AcksWithoutCompletingTwice()
    {
        await using var db = CreateDb();
        db.AccountCleanupSagas.Add(new AccountCleanupSaga
        {
            UserId = 5,
            EventId = "src",
            Status = AccountCleanupSagaStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var (worker, acked, naked) = CreateWorker(db);
        var evt = new RealtimeEvent
        {
            EventId = "cleanup-done:src",
            Type = RealtimeEventType.AccountCleanupCompleted,
            TargetUserId = 5,
            OccurredAtMs = 1,
        };

        await InvokeHandleAsync(worker, CreateDelivery(evt, 1, acked, naked));
        await InvokeHandleAsync(worker, CreateDelivery(evt, 2, acked, naked));

        Assert.Equal(2, acked.Count);
        Assert.Empty(naked);
        Assert.Equal(AccountCleanupSagaStatus.Completed,
            (await db.AccountCleanupSagas.SingleAsync()).Status);
        Assert.Equal(1, await db.AccountCleanupInbox.CountAsync());
    }

    private static (AccountCleanupSagaWorker Worker, List<string> Acked, List<string> Naked)
        CreateWorker(UserDbContext db, int maxMissing = 5)
    {
        var acked = new List<string>();
        var naked = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddScoped<IAccountCleanupSagaService>(_ =>
            new AccountCleanupSagaService(db, NullLogger<AccountCleanupSagaService>.Instance));
        services.AddScoped<AccountCleanupSagaService>(sp =>
            (AccountCleanupSagaService)sp.GetRequiredService<IAccountCleanupSagaService>());
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var worker = new AccountCleanupSagaWorker(
            scopeFactory,
            bus: null,
            Options.Create(new AccountCleanupSagaOptions
            {
                MaxMissingSagaDeliveries = maxMissing,
                MissingSagaNakDelaySeconds = 1,
                PendingTimeoutHours = 0,
            }),
            NullLogger<AccountCleanupSagaWorker>.Instance);
        return (worker, acked, naked);
    }

    private static RealtimeEventDelivery CreateDelivery(
        RealtimeEvent evt,
        ulong deliveryCount,
        List<string> acked,
        List<string> naked)
        => new(
            evt,
            ack: _ =>
            {
                acked.Add(evt.EventId);
                return ValueTask.CompletedTask;
            },
            nak: (_, _) =>
            {
                naked.Add(evt.EventId);
                return ValueTask.CompletedTask;
            },
            deliveryCount);

    private static Task InvokeHandleAsync(AccountCleanupSagaWorker worker, RealtimeEventDelivery delivery)
    {
        var method = typeof(AccountCleanupSagaWorker)
            .GetMethod("HandleDeliveryAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Task)method!.Invoke(worker, [delivery, CancellationToken.None])!;
    }

    private static UserDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new UserDbContext(options);
    }
}
