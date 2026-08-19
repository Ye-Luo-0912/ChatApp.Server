using ChatApp.Realtime.Abstractions.Events;
using ChatApp.Realtime.Abstractions.Relationships;
using ChatApp.Server.IntegrationTests.Support;
using Core.Models.Friend;
using Core.Models.Identity;
using Infrastructure.Services;
using Infrastructure.Services.Utilities;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Friend;

[Collection(nameof(PostgresCollection))]
public sealed class RelationshipProjectionSnapshotReaderTests(PostgresTestFixture postgres)
{
    [SkippableFact]
    public async Task ReadStream_CapturesAuthoritativeRowsAndMatchingVersion()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        var ids = new TsidGeneratorService();
        var owner = ids.GenerateTsid();
        var friend = ids.GenerateTsid();
        var requester = ids.GenerateTsid();
        var blocked = ids.GenerateTsid();
        var now = DateTime.UtcNow;
        await using (var seed = postgres.CreateContext())
        {
            seed.Users.AddRange(
                CreateUser(owner, "snapshot-owner"),
                CreateUser(friend, "snapshot-friend"),
                CreateUser(requester, "snapshot-requester"),
                CreateUser(blocked, "snapshot-blocked"));
            seed.Friendships.Add(new UserFriendEntry
            {
                FriendshipId = ids.GenerateTsid(),
                UserId = owner,
                FriendId = friend,
                CreatedAt = now
            });
            seed.FriendRequests.Add(new FriendRequest
            {
                RequestId = ids.GenerateTsid(),
                RequesterId = requester,
                TargetUserId = owner,
                Status = RequestStatus.Pending,
                Message = "hello",
                CreatedAt = now
            });
            seed.BlockRecords.Add(new BlockRecord
            {
                BlockId = ids.GenerateTsid(),
                BlockerId = owner,
                BlockedUserId = blocked,
                BlockedAt = now
            });
            seed.RelationshipProjectionVersions.Add(new RelationshipProjectionVersion
            {
                OwnerUserId = owner,
                ListType = (byte)RelationshipProjectionListType.Friends,
                Version = 5,
                UpdatedAtMs = new DateTimeOffset(now).ToUnixTimeMilliseconds()
            });
            await seed.SaveChangesAsync();
        }

        await using var db = postgres.CreateContext();
        var reader = new RelationshipProjectionSnapshotReader(db);
        var friends = await reader.ReadStreamAsync(owner, RelationshipProjectionListType.Friends);
        var friendsDigest = await reader.ReadStreamDigestAsync(
            owner,
            RelationshipProjectionListType.Friends);
        var requests = await reader.ReadStreamAsync(owner, RelationshipProjectionListType.FriendRequests);
        var blocks = await reader.ReadStreamAsync(owner, RelationshipProjectionListType.BlockedUsers);
        var streams = await reader.ListStreamsAsync(
            owner - 1,
            RelationshipProjectionListType.BlockedUsers,
            20);

        Assert.Equal(5, friends.Version);
        Assert.Equal(friend.ToString(), Assert.Single(friends.Items).ResourceId);
        Assert.Equal(friends.OwnerUserId, friendsDigest.OwnerUserId);
        Assert.Equal(friends.ListType, friendsDigest.ListType);
        Assert.Equal(friends.Version, friendsDigest.Version);
        Assert.Equal(friends.ItemCount, friendsDigest.ItemCount);
        Assert.Equal(friends.ResourceHash, friendsDigest.ResourceHash);
        Assert.Equal(0, requests.Version);
        Assert.Equal($"{Math.Min(owner, requester)}:{Math.Max(owner, requester)}", Assert.Single(requests.Items).ResourceId);
        Assert.Equal("hello", requests.Items[0].Message);
        Assert.Equal(0, blocks.Version);
        Assert.Equal(blocked.ToString(), Assert.Single(blocks.Items).ResourceId);
        Assert.Contains(streams.Items, item =>
            item.OwnerUserId == owner
            && item.ListType == RelationshipProjectionListType.FriendRequests
            && item.Version == 0);
        Assert.Contains(streams.Items, item =>
            item.OwnerUserId == owner
            && item.ListType == RelationshipProjectionListType.Friends
            && item.Version == 5);
        Assert.Contains(streams.Items, item =>
            item.OwnerUserId == owner
            && item.ListType == RelationshipProjectionListType.BlockedUsers
            && item.Version == 0);

        foreach (var snapshot in new[] { friends, requests, blocks })
        {
            Assert.Equal(snapshot.Items.Count, snapshot.ItemCount);
            Assert.Equal(
                RelationshipProjectionSnapshotHash.Compute(
                    snapshot.Items.Select(static item => item.ResourceId)),
                snapshot.ResourceHash);
            Assert.Equal(
                RelationshipEventIdFactory.CreateRelationshipProjectionSnapshotId(
                    snapshot.OwnerUserId,
                    snapshot.ListType,
                    snapshot.Version,
                    snapshot.ResourceHash),
                snapshot.SnapshotId);
        }
    }

    [SkippableFact]
    public async Task ReadStream_ExportsEmptyVersionedListInsteadOfDroppingItsWatermark()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        var owner = new TsidGeneratorService().GenerateTsid();
        await using (var seed = postgres.CreateContext())
        {
            seed.RelationshipProjectionVersions.Add(new RelationshipProjectionVersion
            {
                OwnerUserId = owner,
                ListType = (byte)RelationshipProjectionListType.BlockedUsers,
                Version = 3,
                UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            await seed.SaveChangesAsync();
        }

        await using var db = postgres.CreateContext();
        var snapshot = await new RelationshipProjectionSnapshotReader(db)
            .ReadStreamAsync(owner, RelationshipProjectionListType.BlockedUsers);

        Assert.Equal(3, snapshot.Version);
        Assert.Empty(snapshot.Items);
        Assert.Equal(0, snapshot.ItemCount);
    }

    [SkippableFact]
    public async Task ListStreams_EnumeratesEmptyUsersSoRebuildCanBaseline()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        var emptyUser = new TsidGeneratorService().GenerateTsid();
        await using (var seed = postgres.CreateContext())
        {
            seed.Users.Add(CreateUser(emptyUser, "snapshot-empty"));
            await seed.SaveChangesAsync();
        }

        await using var db = postgres.CreateContext();
        var reader = new RelationshipProjectionSnapshotReader(db);

        // 无任何关系数据的用户也必须枚举出三份流（version=0），
        // 否则 Realtime rebuild 不会为其建立空快照基线，TCP 读取永远 Unavailable。
        var streams = await reader.ListStreamsAsync(
            emptyUser - 1,
            RelationshipProjectionListType.BlockedUsers,
            20);
        Assert.Contains(streams.Items, item =>
            item.OwnerUserId == emptyUser
            && item.ListType == RelationshipProjectionListType.Friends
            && item.Version == 0);
        Assert.Contains(streams.Items, item =>
            item.OwnerUserId == emptyUser
            && item.ListType == RelationshipProjectionListType.FriendRequests
            && item.Version == 0);
        Assert.Contains(streams.Items, item =>
            item.OwnerUserId == emptyUser
            && item.ListType == RelationshipProjectionListType.BlockedUsers
            && item.Version == 0);

        var snapshot = await reader.ReadStreamAsync(
            emptyUser,
            RelationshipProjectionListType.Friends);
        Assert.Equal(0, snapshot.Version);
        Assert.Equal(0, snapshot.ItemCount);
        Assert.Empty(snapshot.Items);
    }

    private static ApplicationUser CreateUser(long id, string prefix)
    {
        var suffix = id.ToString();
        return new ApplicationUser
        {
            Id = id,
            UserName = $"{prefix}-{suffix}",
            NormalizedUserName = $"{prefix}-{suffix}".ToUpperInvariant(),
            Email = $"{prefix}-{suffix}@example.com",
            NormalizedEmail = $"{prefix}-{suffix}@example.com".ToUpperInvariant(),
            EmailConfirmed = true,
            FriendRequestPolicy = FriendRequestPolicy.RequireVerification
        };
    }
}
