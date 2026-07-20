using ChatApp.Server.IntegrationTests.Support;
using Core.Models.Friend;
using Core.Models.Identity;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Database;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Database")]
public sealed class NormalizedUserUniqueTests(PostgresTestFixture postgres)
{
    [SkippableFact]
    public async Task NormalizedEmail_UniqueConstraint_RejectsDuplicate()
    {
        Skip.IfNot(postgres.IsAvailable, postgres.SkipReason ?? "PostgreSQL not available");

        await using var context = postgres.CreateContext();

        var email = $"dup-{Guid.NewGuid():N}@example.com";
        var normalized = email.ToUpperInvariant();

        context.Users.AddRange(
            CreateUser(1_000_001, "user-a", email, normalized),
            CreateUser(1_000_002, "user-b", "other@example.com", normalized));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task GetFriendsAsync_RespectsTakeLimitAndCursor()
    {
        Skip.IfNot(postgres.IsAvailable, postgres.SkipReason ?? "PostgreSQL not available");

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ownerId = 2_000_001L + Random.Shared.Next(0, 100_000);
        var friendIds = new[]
        {
            ownerId + 10, ownerId + 11, ownerId + 12, ownerId + 13, ownerId + 14,
        };
        var friendshipIds = new[]
        {
            ownerId + 100, ownerId + 101, ownerId + 102, ownerId + 103, ownerId + 104,
        };

        await using (var seedContext = postgres.CreateContext())
        {
            seedContext.Users.Add(CreateUser(ownerId, $"owner-{suffix}", $"owner-{suffix}@example.com"));
            for (var i = 0; i < friendIds.Length; i++)
            {
                var friendId = friendIds[i];
                seedContext.Users.Add(CreateUser(friendId, $"friend-{suffix}-{i}", $"friend-{suffix}-{i}@example.com"));
                seedContext.Friendships.Add(new UserFriendEntry
                {
                    FriendshipId = friendshipIds[i],
                    UserId = ownerId,
                    FriendId = friendId,
                    CreatedAt = DateTime.UtcNow
                });
            }

            await seedContext.SaveChangesAsync();
        }

        await using var queryContext = postgres.CreateContext();
        var service = new FriendshipService(
            queryContext,
            new NoopCacheProvider(),
            NullLogger<FriendshipService>.Instance);

        var firstPage = await service.GetFriendsAsync(
            ownerId,
            f => new FriendDto { FriendId = f.FriendId },
            limit: 2);

        Assert.Equal(2, firstPage.Items.Count);
        Assert.True(firstPage.HasMore);
        Assert.NotNull(firstPage.NextCursor);
        Assert.Equal(friendIds[1].ToString(), firstPage.NextCursor);

        var secondPage = await service.GetFriendsAsync(
            ownerId,
            f => new FriendDto { FriendId = f.FriendId },
            cursor: firstPage.NextCursor,
            limit: 2);

        Assert.Equal(2, secondPage.Items.Count);
        Assert.True(secondPage.HasMore);
        Assert.Equal(friendIds[3].ToString(), secondPage.NextCursor);

        var thirdPage = await service.GetFriendsAsync(
            ownerId,
            f => new FriendDto { FriendId = f.FriendId },
            cursor: secondPage.NextCursor,
            limit: 2);

        Assert.Single(thirdPage.Items);
        Assert.False(thirdPage.HasMore);
        Assert.Null(thirdPage.NextCursor);
        Assert.Equal(friendIds[4], thirdPage.Items[0].FriendId);
    }

    private static ApplicationUser CreateUser(
        long id,
        string userName,
        string email,
        string? normalizedEmail = null)
    {
        var normalized = normalizedEmail ?? email.ToUpperInvariant();
        return new ApplicationUser
        {
            Id = id,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = normalized,
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            CreatedDate = DateTimeOffset.UtcNow
        };
    }
}
