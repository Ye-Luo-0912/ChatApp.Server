using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models.Identity;
using Core.Models.Moderation;
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
        var (reporter, sender) = await SeedPairAsync(db, "rep");

        var messageId = $"msg-{Guid.NewGuid():N}";
        var provider = new FakeMessageEvidenceProvider(new MessageEvidenceSnapshot(
            messageId,
            sender.Id,
            reporter.Id,
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
        Assert.Contains("ReceiverUserId", report.EvidenceSnapshot, StringComparison.Ordinal);
        Assert.Contains("abc123hash", report.EvidenceSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain(forged, report.EvidenceSnapshot, StringComparison.Ordinal);
        Assert.Equal(forged, report.Detail);
    }

    [SkippableFact]
    public async Task MessageReport_Rejects_NonParticipant()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var (reporter, _) = await SeedPairAsync(db, "np");
        var outsider = await SeedUserAsync(db, "out");
        var messageId = $"msg-{Guid.NewGuid():N}";
        var provider = new FakeMessageEvidenceProvider(new MessageEvidenceSnapshot(
            messageId, outsider.Id, outsider.Id + 1, DateTimeOffset.UtcNow, "h", "body"));

        var moderation = new ModerationService(
            db, new NoopSessionStore(),
            new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance),
            provider, NullLogger<ModerationService>.Instance);

        var result = await moderation.ReportAsync(
            reporter.Id, UserReportTargetType.Message, null, messageId, "abuse", "x");
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "Forbidden");
    }

    [SkippableFact]
    public async Task MessageReport_Rejects_TargetMismatch()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var (reporter, sender) = await SeedPairAsync(db, "mm");
        var other = await SeedUserAsync(db, "oth");
        var messageId = $"msg-{Guid.NewGuid():N}";
        var provider = new FakeMessageEvidenceProvider(new MessageEvidenceSnapshot(
            messageId, sender.Id, reporter.Id, DateTimeOffset.UtcNow, "h", "body"));

        var moderation = new ModerationService(
            db, new NoopSessionStore(),
            new SecurityEventStore(db, NullLogger<SecurityEventStore>.Instance),
            provider, NullLogger<ModerationService>.Instance);

        var result = await moderation.ReportAsync(
            reporter.Id, UserReportTargetType.Message, other.Id, messageId, "abuse", "x");
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "TargetMismatch");
    }

    [SkippableFact]
    public async Task MessageReport_Fails_WhenEvidenceUnavailable()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var reporter = await SeedUserAsync(db, "reu");

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

    private async Task<(ApplicationUser A, ApplicationUser B)> SeedPairAsync(UserDbContext db, string prefix)
    {
        var a = await SeedUserAsync(db, prefix + "a");
        var b = await SeedUserAsync(db, prefix + "b");
        return (a, b);
    }

    private static async Task<ApplicationUser> SeedUserAsync(UserDbContext db, string prefix)
    {
        var tsid = new TsidGeneratorService();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var hasher = new Infrastructure.Services.Auth.BcryptPasswordHasher();
        var user = new ApplicationUser
        {
            Id = tsid.GenerateTsid(),
            UserName = $"{prefix}-{suffix}",
            NormalizedUserName = $"{prefix}-{suffix}".ToUpperInvariant(),
            Email = $"{prefix}-{suffix}@ex.com",
            NormalizedEmail = $"{prefix}-{suffix}@EX.COM",
            EmailConfirmed = true,
            PasswordHash = await hasher.HashPasswordAsync("Passw0rd!"),
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private sealed class FakeMessageEvidenceProvider(MessageEvidenceSnapshot snapshot) : IMessageEvidenceProvider
    {
        public Task<MessageEvidenceSnapshot?> TryGetAsync(
            string messageId, long? requestingUserId = null, CancellationToken cancellationToken = default)
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
