using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
using Core.Models.Email;
using Core.Models.Notifications;
using Core.Models.Security;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Friend;

[Collection(nameof(PostgresCollection))]
public sealed class NotificationOutboxBatchTests(PostgresTestFixture postgres)
{
    [SkippableFact]
    public async Task BatchDelivery_ConflictDoesNotDiscardOtherNotifications()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            CreateOutbox(now, "conflict"),
            CreateOutbox(now, "new"),
        };
        db.NotificationOutbox.AddRange(rows);
        await db.SaveChangesAsync();

        var dispatcher = new NotificationOutboxDispatcher(
            db,
            new NoopEmailSender(),
            new NotificationOutboxMetrics(),
            NullLogger.Instance);
        var claimed = await dispatcher.ClaimDueItemsAsync(10, CancellationToken.None);
        var batch = claimed.Where(x => rows.Select(r => r.Id).Contains(x.Id)).ToList();
        Assert.Equal(2, batch.Count);

        await using (var racingDb = postgres.CreateContext())
        {
            racingDb.InAppNotifications.Add(new InAppNotification
            {
                UserId = rows[0].UserId,
                Type = rows[0].Type,
                Title = rows[0].Title,
                Body = rows[0].Body,
                SourceOutboxId = rows[0].Id,
                CreatedAt = now,
            });
            await racingDb.SaveChangesAsync();
        }

        await dispatcher.DeliverInAppBatchAsync(batch, CancellationToken.None);

        await using var check = postgres.CreateContext();
        var sourceIds = rows.Select(x => x.Id).ToArray();
        Assert.Equal(2, await check.InAppNotifications.CountAsync(
            x => x.SourceOutboxId != null && sourceIds.Contains(x.SourceOutboxId.Value)));
        Assert.Equal(2, await check.NotificationOutbox.CountAsync(
            x => sourceIds.Contains(x.Id) && x.InAppDeliveredAt != null));
    }

    private static NotificationOutboxItem CreateOutbox(DateTimeOffset now, string suffix) => new()
    {
        UserId = Random.Shared.NextInt64(1, long.MaxValue),
        Type = "test",
        Title = "batch-" + suffix,
        Body = "body",
        Status = NotificationOutboxStatus.Pending,
        IdempotencyKey = $"batch-test:{Guid.NewGuid():N}",
        CreatedAt = now,
        UpdatedAt = now,
        NextAttemptAt = now,
    };

    private sealed class NoopEmailSender : IEmailSender
    {
        private static readonly EmailResult Success = new() { IsSuccess = true };

        public Task<EmailResult> SendEmailAsync(
            string to, string subject, string body, bool isHtml = true,
            CancellationToken cancellation = default) => Task.FromResult(Success);

        public Task<EmailResult> EnqueueEmailAsync(
            string to, string subject, string body, bool isHtml = true,
            string? emailType = null, string? idempotencyKey = null,
            CancellationToken cancellation = default) => Task.FromResult(Success);

        public Task<EmailResult> SendVerificationEmailAsync(
            string to, string username, string verificationToken,
            CancellationToken cancellation) => Task.FromResult(Success);
    }
}
