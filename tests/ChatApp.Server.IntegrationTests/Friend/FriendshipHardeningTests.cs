using System.Security.Claims;
using ChatApp.Server.IntegrationTests.Support;
using ChatApp.Server.RateLimiting;
using Core.Models.Friend;
using Core.Settings;
using Infrastructure.Data;
using Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Friend;

public sealed class FriendshipHardeningTests
{
    [Fact]
    public async Task SendRequest_RejectsNonPositiveTargetAndOversizedMessageBeforePersistence()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var invalidTarget = await service.SendRequestAsync(1, 0);
        var oversizedMessage = await service.SendRequestAsync(
            1, 2, new string('x', FriendshipInputLimits.FriendRequestMessageMaxLength + 1));

        Assert.False(invalidTarget.IsSuccess);
        Assert.Equal(FriendshipOperationResultErrorCode.ValidationFailed, invalidTarget.ErrorCode);
        Assert.False(oversizedMessage.IsSuccess);
        Assert.Equal(FriendshipOperationResultErrorCode.ValidationFailed, oversizedMessage.ErrorCode);
        Assert.Empty(db.FriendRequests);
    }

    [Fact]
    public async Task BlockUser_RejectsMissingTargetWithoutCreatingABlockRecord()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var result = await service.BlockUserAsync(1, 2);

        Assert.False(result.IsSuccess);
        Assert.Equal(FriendshipOperationResultErrorCode.ValidationFailed, result.ErrorCode);
        Assert.Empty(db.BlockRecords);
    }

    [Fact]
    public async Task UpdateNote_RejectsOversizedNoteBeforeQueryingPersistence()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var result = await service.UpdateFriendNoteAsync(
            1, 2, new string('x', FriendshipInputLimits.FriendNoteMaxLength + 1));

        Assert.False(result.IsSuccess);
        Assert.Equal(FriendshipOperationResultErrorCode.ValidationFailed, result.ErrorCode);
    }

    [Fact]
    public void FriendshipTextBounds_ArePresentInTheEfModel()
    {
        using var db = CreateDb();

        var request = db.Model.FindEntityType(typeof(FriendRequest))
            ?? throw new InvalidOperationException("FriendRequest is missing from the EF model.");
        var friendship = db.Model.FindEntityType(typeof(UserFriendEntry))
            ?? throw new InvalidOperationException("UserFriendEntry is missing from the EF model.");

        Assert.Equal(
            FriendshipInputLimits.FriendRequestMessageMaxLength,
            request.FindProperty(nameof(FriendRequest.Message))!.GetMaxLength());
        Assert.Equal(
            FriendshipInputLimits.FriendNoteMaxLength,
            friendship.FindProperty(nameof(UserFriendEntry.Note))!.GetMaxLength());
    }

    [Fact]
    public async Task FriendshipWritePolicy_UsesAnIndependentAuthenticatedUserPartition()
    {
        var provider = new RateLimitPolicyProvider(Options.Create(new RateLimitingOptions
        {
            UserSensitivePermitLimit = 7,
            UserSensitiveWindowSeconds = 45,
        }));

        var policy = provider.Get("friendship-write")
            ?? throw new InvalidOperationException("friendship-write policy is missing.");
        Assert.Equal(7, policy.PermitLimit);
        Assert.Equal(TimeSpan.FromSeconds(45), policy.Window);
        var dimension = Assert.Single(policy.Dimensions);
        Assert.Equal("k", dimension.KeySuffix);

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "42")], "test")),
        };

        Assert.Equal("uid:42", await dimension.ExtractKeyAsync(context));
    }

    private static FriendshipService CreateService(UserDbContext db) => new(
        db,
        new NoopCacheProvider(),
        NullLogger<FriendshipService>.Instance);

    private static UserDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new UserDbContext(options);
    }
}
