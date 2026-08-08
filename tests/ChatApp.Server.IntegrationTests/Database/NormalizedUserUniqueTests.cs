using ChatApp.Server.IntegrationTests.Support;
using Core.Models.Identity;
using Microsoft.EntityFrameworkCore;
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
