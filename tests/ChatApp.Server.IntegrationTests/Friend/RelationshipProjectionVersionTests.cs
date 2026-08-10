using ChatApp.Realtime.Abstractions.Relationships;
using ChatApp.Realtime.Integration.Serialization;
using ChatApp.Server.IntegrationTests.Support;
using Core.Models.Identity;
using Infrastructure.Services;
using Infrastructure.Services.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Friend;

[Collection(nameof(PostgresCollection))]
public sealed class RelationshipProjectionVersionTests(PostgresTestFixture postgres)
{
    [SkippableFact]
    public async Task ConcurrentPairWrites_AllocateOneContiguousVersionPerOwnerList()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        var ids = new TsidGeneratorService();
        var ownerId = ids.GenerateTsid();
        var target1Id = ids.GenerateTsid();
        var target2Id = ids.GenerateTsid();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var seed = postgres.CreateContext())
        {
            seed.Users.AddRange(
                CreateUser(ownerId, $"owner-{suffix}"),
                CreateUser(target1Id, $"target1-{suffix}"),
                CreateUser(target2Id, $"target2-{suffix}"));
            await seed.SaveChangesAsync();
        }

        async Task<bool> SendAsync(long targetId)
        {
            await using var db = postgres.CreateContext();
            var service = new FriendshipService(
                db,
                new NoopCacheProvider(),
                NullLogger<FriendshipService>.Instance);
            return (await service.SendRequestAsync(ownerId, targetId, "hello")).IsSuccess;
        }

        var results = await Task.WhenAll(SendAsync(target1Id), SendAsync(target2Id));
        Assert.All(results, Assert.True);

        await using var verify = postgres.CreateContext();
        var ownerVersion = await verify.RelationshipProjectionVersions.AsNoTracking()
            .Where(row => row.OwnerUserId == ownerId
                          && row.ListType == (byte)RelationshipProjectionListType.FriendRequests)
            .Select(row => row.Version)
            .SingleAsync();
        Assert.Equal(2, ownerVersion);

        var ownerEvents = await verify.RealtimeOutbox.AsNoTracking()
            .Where(item => item.TargetUserId == ownerId)
            .Select(item => item.PayloadJson)
            .ToArrayAsync();
        var versions = ownerEvents
            .Select(RealtimeWireSerializer.DeserializeEvent)
            .Select(evt => RealtimeWireSerializer.DeserializeDomainNotification(evt!.PayloadJson!))
            .Select(payload => payload!.Projection!.Version)
            .Order()
            .ToArray();
        Assert.Equal([1L, 2L], versions);
    }

    private static ApplicationUser CreateUser(long id, string name) => new()
    {
        Id = id,
        UserName = name,
        NormalizedUserName = name.ToUpperInvariant(),
        Email = $"{name}@example.com",
        NormalizedEmail = $"{name.ToUpperInvariant()}@EXAMPLE.COM",
        EmailConfirmed = true,
        FriendRequestPolicy = FriendRequestPolicy.RequireVerification
    };
}
