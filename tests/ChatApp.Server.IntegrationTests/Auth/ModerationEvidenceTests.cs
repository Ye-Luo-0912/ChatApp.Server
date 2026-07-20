using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models.Identity;
using Core.Models.Moderation;
using Core.Models.Security;
using Infrastructure.Data;
using Infrastructure.Services;
using Infrastructure.Services.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisPostgresCollection))]
public sealed class ModerationEvidenceTests(PostgresTestFixture postgres)
{
    [SkippableFact]
    public async Task MessageReport_UsesServerEvidence_NotClientDetail()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var tsid = new TsidGeneratorService();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var hasher = new Infrastructure.Services.Auth.BcryptPasswordHasher();

        var reporter = new ApplicationUser
        {
            Id = tsid.GenerateTsid(),
            UserName = $"rep-{suffix}",
            NormalizedUserName = $"REP-{suffix}".ToUpperInvariant(),
            Email = $"rep-{suffix}@ex.com",
            NormalizedEmail = $"REP-{suffix}@EX.COM",
            EmailConfirmed = true,
            PasswordHash = hasher.HashPassword("Passw0rd!"),
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        var sender = new ApplicationUser
        {
            Id = tsid.GenerateTsid(),
            UserName = $"snd-{suffix}",
            NormalizedUserName = $"SND-{suffix}".ToUpperInvariant(),
            Email = $"snd-{suffix}@ex.com",
            NormalizedEmail = $"SND-{suffix}@EX.COM",
            EmailConfirmed = true,
            PasswordHash = hasher.HashPassword("Passw0rd!"),
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        db.Users.AddRange(reporter, sender);
        await db.SaveChangesAsync();

        var messageId = $"msg-{Guid.NewGuid():N}";
        var provider = new FakeMessageEvidenceProvider(new MessageEvidenceSnapshot(
            messageId,
            sender.Id,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            "abc123hash",
            "server-side original body"));

        var moderation = new ModerationService(
            db,
            new NoopSessionStore(),
            new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance),
            provider,
            NullLogger<ModerationService>.Instance);

        var forged = "FORGED_CLIENT_DETAIL_SHOULD_NOT_APPEAR";
        var result = await moderation.ReportAsync(
            reporter.Id,
            UserReportTargetType.Message,
            targetUserId: null,
            targetMessageId: messageId,
            reason: "abuse",
            detail: forged);

        Assert.True(result.Succeeded);

        var report = await db.UserReports.AsNoTracking()
            .SingleAsync(r => r.ReporterId == reporter.Id && r.TargetMessageId == messageId);
        Assert.Equal(sender.Id, report.TargetUserId);
        Assert.Contains("server-side original body", report.EvidenceSnapshot, StringComparison.Ordinal);
        Assert.Contains("abc123hash", report.EvidenceSnapshot, StringComparison.Ordinal);
        Assert.Contains("message-service", report.EvidenceSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain(forged, report.EvidenceSnapshot, StringComparison.Ordinal);
        Assert.Equal(forged, report.Detail);
    }

    [SkippableFact]
    public async Task MessageReport_Fails_WhenEvidenceUnavailable()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var tsid = new TsidGeneratorService();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var hasher = new Infrastructure.Services.Auth.BcryptPasswordHasher();
        var reporter = new ApplicationUser
        {
            Id = tsid.GenerateTsid(),
            UserName = $"reu-{suffix}",
            NormalizedUserName = $"REU-{suffix}".ToUpperInvariant(),
            Email = $"reu-{suffix}@ex.com",
            NormalizedEmail = $"REU-{suffix}@EX.COM",
            EmailConfirmed = true,
            PasswordHash = hasher.HashPassword("Passw0rd!"),
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        db.Users.Add(reporter);
        await db.SaveChangesAsync();

        var moderation = new ModerationService(
            db,
            new NoopSessionStore(),
            new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance),
            new UnavailableMessageEvidenceProvider(NullLogger<UnavailableMessageEvidenceProvider>.Instance),
            NullLogger<ModerationService>.Instance);

        var result = await moderation.ReportAsync(
            reporter.Id,
            UserReportTargetType.Message,
            null,
            "missing-msg",
            "abuse",
            "client detail");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "EvidenceUnavailable");
        Assert.False(await db.UserReports.AnyAsync(r => r.ReporterId == reporter.Id));
    }

    private sealed class FakeMessageEvidenceProvider(MessageEvidenceSnapshot snapshot) : IMessageEvidenceProvider
    {
        public Task<MessageEvidenceSnapshot?> TryGetAsync(string messageId, CancellationToken cancellationToken = default)
            => Task.FromResult<MessageEvidenceSnapshot?>(
                string.Equals(messageId, snapshot.MessageId, StringComparison.Ordinal) ? snapshot : null);
    }

    private sealed class NoopSessionStore : ISessionStore
    {
        public Task<Core.Models.Token.SessionRecord?> GetSessionAsync(
            string userId, string deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult<Core.Models.Token.SessionRecord?>(null);

        public Task<IReadOnlyList<Core.Models.Token.SessionRecord>> ListSessionsAsync(
            string userId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Core.Models.Token.SessionRecord>>([]);

        public Task RevokeSessionAsync(string userId, string deviceId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<int> RevokeAllSessionsAsync(
            string userId, string? exceptDeviceId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }
}
