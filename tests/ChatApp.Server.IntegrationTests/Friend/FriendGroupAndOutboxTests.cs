using ChatApp.Server.IntegrationTests.Support;
using Core.Models.Email;
using Core.Models.Friend;
using Infrastructure.Data;
using Infrastructure.Services;
using Infrastructure.Services.Email;
using Infrastructure.Services.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Friend;

[Collection(nameof(RedisPostgresCollection))]
public sealed class FriendGroupAndOutboxTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [SkippableFact]
    public async Task FriendGroups_OwnershipRenameDeleteAndPaging()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var friendship = new FriendshipService(db, redis.Cache, NullLogger<FriendshipService>.Instance);

        var ownerId = new TsidGeneratorService().GenerateTsid();
        var otherId = new TsidGeneratorService().GenerateTsid();
        var friendId = new TsidGeneratorService().GenerateTsid();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        db.Users.AddRange(
            new Core.Models.Identity.ApplicationUser
            {
                Id = ownerId, UserName = $"owner-{suffix}", NormalizedUserName = $"OWNER-{suffix}".ToUpperInvariant(),
                Email = $"owner-{suffix}@ex.com", NormalizedEmail = $"OWNER-{suffix}@EX.COM", EmailConfirmed = true,
            },
            new Core.Models.Identity.ApplicationUser
            {
                Id = otherId, UserName = $"other-{suffix}", NormalizedUserName = $"OTHER-{suffix}".ToUpperInvariant(),
                Email = $"other-{suffix}@ex.com", NormalizedEmail = $"OTHER-{suffix}@EX.COM", EmailConfirmed = true,
            },
            new Core.Models.Identity.ApplicationUser
            {
                Id = friendId, UserName = $"buddy-{suffix}", NormalizedUserName = $"BUDDY-{suffix}".ToUpperInvariant(),
                Email = $"buddy-{suffix}@ex.com", NormalizedEmail = $"BUDDY-{suffix}@EX.COM", EmailConfirmed = true,
            });
        await db.SaveChangesAsync();

        var created = await friendship.CreateGroupAsync(ownerId, $"同事-{suffix}");
        Assert.True(created.Succeeded);
        var groupId = created.Data!.GroupId;

        var stolen = await friendship.RenameGroupAsync(otherId, groupId, "黑客");
        Assert.False(stolen.IsSuccess);
        Assert.Equal(FriendshipOperationResultErrorCode.FriendGroupNotFound, stolen.ErrorCode);

        var dup = await friendship.CreateGroupAsync(ownerId, $"同事-{suffix}");
        Assert.False(dup.Succeeded);
        Assert.Equal(FriendshipOperationResultErrorCode.FriendGroupNameConflict, dup.ErrorCode);

        db.Friendships.Add(new UserFriendEntry
        {
            UserId = ownerId,
            FriendId = friendId,
            GroupId = groupId,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var page = await friendship.GetFriendsInGroupAsync(ownerId, groupId, limit: 10);
        Assert.Single(page.Items);

        Assert.True((await friendship.DeleteGroupAsync(ownerId, groupId)).IsSuccess);
        var friendshipRow = await db.Friendships.IgnoreQueryFilters()
            .SingleAsync(f => f.UserId == ownerId && f.FriendId == friendId);
        Assert.Null(friendshipRow.GroupId);
        Assert.False(await db.FriendGroups.AnyAsync(g => g.GroupId == groupId));
    }

    [SkippableFact]
    public async Task Outbox_IdempotencyKey_OnlyCountsMatchingConstraint()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        var factory = new TestScopeFactory(postgres.ConnectionString);
        var sender = new QueuedEmailSender(factory, new TsidGeneratorService(), new EmailOutboxMetrics());

        var key = $"otp:test:{Guid.NewGuid():N}";
        Assert.True((await sender.EnqueueEmailAsync("a@b.com", "s", "body", idempotencyKey: key)).IsSuccess);
        Assert.True((await sender.EnqueueEmailAsync("a@b.com", "s", "body", idempotencyKey: key)).IsSuccess);

        await using var check = postgres.CreateContext();
        Assert.Equal(1, await check.EmailOutbox.CountAsync(x => x.IdempotencyKey == key));
    }

    [SkippableFact]
    public async Task OutboxWorker_TwoClaimers_OnlyOneSucceeds()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        var factory = new TestScopeFactory(postgres.ConnectionString);
        var metrics = new EmailOutboxMetrics();

        // 共享库可能残留大量 Pending；先清掉以免占满 claim 批次。
        await ClearClaimableOutboxAsync();

        var itemId = await SeedPendingAsync(factory, "compete@ex.com");

        var d1 = CreateDispatcher(factory, metrics, ownerId: "worker-a", sendOk: true);
        var d2 = CreateDispatcher(factory, metrics, ownerId: "worker-b", sendOk: true);

        var c1 = await d1.ClaimDueItemsAsync(CancellationToken.None);
        var c2 = await d2.ClaimDueItemsAsync(CancellationToken.None);

        var claimedByA = c1.Any(x => x.Id == itemId);
        var claimedByB = c2.Any(x => x.Id == itemId);
        Assert.True(claimedByA ^ claimedByB, "exactly one worker should claim the row");

        var winner = claimedByA ? d1 : d2;
        var claimed = claimedByA ? c1 : c2;
        await winner.ProcessItemAsync(claimed.Single(x => x.Id == itemId), CancellationToken.None);

        await using var check = postgres.CreateContext();
        var row = await check.EmailOutbox.AsNoTracking().SingleAsync(x => x.Id == itemId);
        Assert.Equal(EmailOutboxStatus.Sent, row.Status);
    }

    [SkippableFact]
    public async Task OutboxWorker_StaleProcessingLease_IsReclaimed()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        var factory = new TestScopeFactory(postgres.ConnectionString);
        var id = await SeedPendingAsync(factory, "lease@ex.com");

        await using (var db = postgres.CreateContext())
        {
            await db.EmailOutbox.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, EmailOutboxStatus.Processing)
                .SetProperty(x => x.LockedAt, DateTime.UtcNow.AddMinutes(-30))
                .SetProperty(x => x.LockOwner, "dead-worker"));
        }

        var dispatcher = CreateDispatcher(factory, new EmailOutboxMetrics(),
            ownerId: "reclaimer", sendOk: true, processingLease: TimeSpan.FromMinutes(5));

        var reclaimed = await dispatcher.ReclaimStaleProcessingAsync(CancellationToken.None);
        Assert.True(reclaimed >= 1);

        await using var check = postgres.CreateContext();
        var row = await check.EmailOutbox.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(EmailOutboxStatus.Failed, row.Status);
        Assert.Null(row.LockedAt);
        Assert.Equal("Processing lease expired", row.LastError);
    }

    [SkippableFact]
    public async Task OutboxWorker_CrashAfterSmtp_AllowsResendAfterReclaim()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        var factory = new TestScopeFactory(postgres.ConnectionString);
        var sendCount = 0;
        Task<EmailResult> Send(string to, string subject, string body, bool html, CancellationToken ct)
        {
            Interlocked.Increment(ref sendCount);
            return Task.FromResult(new EmailResult { IsSuccess = true });
        }

        var metrics = new EmailOutboxMetrics();
        var dispatcher = new EmailOutboxDispatcher(
            factory, Send, metrics, NullLogger.Instance, ownerId: "crash-worker",
            processingLease: TimeSpan.FromMilliseconds(1));

        var id = await SeedPendingAsync(factory, "crash@ex.com");
        var claimed = await dispatcher.ClaimDueItemsAsync(CancellationToken.None);
        var item = Assert.Single(claimed, x => x.Id == id);

        await dispatcher.SimulateCrashAfterSendBeforePersistAsync(item, CancellationToken.None);
        Assert.Equal(1, sendCount);

        await using (var mid = postgres.CreateContext())
        {
            var stuck = await mid.EmailOutbox.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(EmailOutboxStatus.Processing, stuck.Status);
        }

        await Task.Delay(20);
        Assert.True(await dispatcher.ReclaimStaleProcessingAsync(CancellationToken.None) >= 1);

        EmailOutboxItem? retryItem = null;
        for (var i = 0; i < 5 && retryItem is null; i++)
        {
            var again = await dispatcher.ClaimDueItemsAsync(CancellationToken.None);
            retryItem = again.FirstOrDefault(x => x.Id == id);
        }

        Assert.NotNull(retryItem);
        await dispatcher.ProcessItemAsync(retryItem, CancellationToken.None);

        Assert.Equal(2, sendCount);
        await using var check = postgres.CreateContext();
        Assert.Equal(EmailOutboxStatus.Sent,
            (await check.EmailOutbox.AsNoTracking().SingleAsync(x => x.Id == id)).Status);
    }

    [SkippableFact]
    public async Task OutboxWorker_CancelDuringSend_DoesNotBumpAttemptCount()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        var factory = new TestScopeFactory(postgres.ConnectionString);
        using var cts = new CancellationTokenSource();

        Task<EmailResult> Send(string to, string subject, string body, bool html, CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new EmailResult { IsSuccess = true });
        }

        var dispatcher = new EmailOutboxDispatcher(
            factory, Send, new EmailOutboxMetrics(), NullLogger.Instance, ownerId: "cancel-worker");

        var id = await SeedPendingAsync(factory, "cancel@ex.com");
        var claimed = await dispatcher.ClaimDueItemsAsync(CancellationToken.None);
        var item = Assert.Single(claimed, x => x.Id == id);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dispatcher.ProcessItemAsync(item, cts.Token));

        await using var check = postgres.CreateContext();
        var row = await check.EmailOutbox.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(0, row.AttemptCount);
        Assert.Equal(EmailOutboxStatus.Pending, row.Status);
        Assert.Null(row.LockedAt);
    }

    [SkippableFact]
    public async Task OutboxWorker_DeadRetrySent_FullFlow()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await ClearClaimableOutboxAsync();

        var factory = new TestScopeFactory(postgres.ConnectionString);
        var failOnce = true;
        Task<EmailResult> Send(string to, string subject, string body, bool html, CancellationToken ct)
        {
            if (failOnce)
            {
                failOnce = false;
                return Task.FromResult(new EmailResult { IsSuccess = false, ErrorMessage = "smtp down" });
            }

            return Task.FromResult(new EmailResult { IsSuccess = true });
        }

        // maxAttempts=1 → 首次失败即 Dead
        var dispatcher = new EmailOutboxDispatcher(
            factory, Send, new EmailOutboxMetrics(), NullLogger.Instance,
            ownerId: "dead-flow", maxAttempts: 1);

        var id = await SeedPendingAsync(factory, "dead@ex.com");
        var item = await ClaimUntilAsync(dispatcher, id);
        await dispatcher.ProcessItemAsync(item, CancellationToken.None);

        await using (var mid = postgres.CreateContext())
        {
            Assert.Equal(EmailOutboxStatus.Dead,
                (await mid.EmailOutbox.AsNoTracking().SingleAsync(x => x.Id == id)).Status);
        }

        await dispatcher.RetryDeadLetterAsync(id, CancellationToken.None);

        var again = await ClaimUntilAsync(dispatcher, id);
        await dispatcher.ProcessItemAsync(again, CancellationToken.None);

        await using var check = postgres.CreateContext();
        Assert.Equal(EmailOutboxStatus.Sent,
            (await check.EmailOutbox.AsNoTracking().SingleAsync(x => x.Id == id)).Status);
    }

    private async Task ClearClaimableOutboxAsync()
    {
        await using var clean = postgres.CreateContext();
        var stale = await clean.EmailOutbox
            .Where(x => x.Status == EmailOutboxStatus.Pending
                        || x.Status == EmailOutboxStatus.Failed
                        || x.Status == EmailOutboxStatus.Dead)
            .ToListAsync();
        if (stale.Count == 0) return;
        clean.EmailOutbox.RemoveRange(stale);
        await clean.SaveChangesAsync();
    }

    private static async Task<EmailOutboxItem> ClaimUntilAsync(
        EmailOutboxDispatcher dispatcher, long id, int attempts = 8)
    {
        for (var i = 0; i < attempts; i++)
        {
            var batch = await dispatcher.ClaimDueItemsAsync(CancellationToken.None);
            var hit = batch.FirstOrDefault(x => x.Id == id);
            if (hit is not null) return hit;
        }

        throw new Xunit.Sdk.XunitException($"ClaimDueItems 未拿到 Id={id}");
    }

    private static EmailOutboxDispatcher CreateDispatcher(
        IServiceScopeFactory factory,
        EmailOutboxMetrics metrics,
        string ownerId,
        bool sendOk,
        TimeSpan? processingLease = null)
    {
        Task<EmailResult> Send(string to, string subject, string body, bool html, CancellationToken ct) =>
            Task.FromResult(new EmailResult
            {
                IsSuccess = sendOk,
                ErrorMessage = sendOk ? null : "fail",
            });

        return new EmailOutboxDispatcher(
            factory, Send, metrics, NullLogger.Instance, ownerId, processingLease);
    }

    private static async Task<long> SeedPendingAsync(IServiceScopeFactory factory, string to)
    {
        var sender = new QueuedEmailSender(factory, new TsidGeneratorService(), new EmailOutboxMetrics());
        var key = $"otp:worker:{Guid.NewGuid():N}";
        Assert.True((await sender.EnqueueEmailAsync(to, "s", "body", idempotencyKey: key)).IsSuccess);

        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await db.EmailOutbox.Where(x => x.IdempotencyKey == key).Select(x => x.Id).SingleAsync();
    }

    private sealed class TestScopeFactory(string connectionString) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            var services = new ServiceCollection();
            services.AddDbContext<UserDbContext>(o => o.UseNpgsql(connectionString));
            var sp = services.BuildServiceProvider();
            return sp.CreateScope();
        }
    }
}
