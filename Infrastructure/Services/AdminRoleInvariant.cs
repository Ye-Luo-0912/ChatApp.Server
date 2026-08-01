using Core.Models.Identity;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>
/// Business-specific serialization fence for mutations that can remove an
/// administrator from the active control plane.
/// </summary>
internal static class AdminRoleInvariant
{
    private const long AdvisoryLockKey = 0x4348415441444D49; // "CHATADMI"

    public static Task AcquireMutationLockAsync(
        UserDbContext db,
        CancellationToken cancellationToken)
    {
        if (db.Database.ProviderName?.Contains(
                "Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            return Task.CompletedTask;

        return db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0})",
            [AdvisoryLockKey],
            cancellationToken);
    }

    public static async Task<bool> IsLastActiveAdminAsync(
        UserDbContext db,
        long targetUserId,
        CancellationToken cancellationToken)
    {
        var normalizedAdmin = KnownRoles.Admin.ToUpperInvariant();
        var roleId = await db.Roles
            .AsNoTracking()
            .Where(r => r.NormalizedName == normalizedAdmin)
            .Select(r => (long?)r.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (roleId is null)
            return false;

        var targetIsAdmin = await db.UserRoles
            .AsNoTracking()
            .AnyAsync(
                ur => ur.UserId == targetUserId && ur.RoleId == roleId.Value,
                cancellationToken);
        if (!targetIsAdmin)
            return false;

        var now = DateTimeOffset.UtcNow;
        return !await db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.RoleId == roleId.Value && ur.UserId != targetUserId)
            .Join(
                db.Users.AsNoTracking(),
                ur => ur.UserId,
                user => user.Id,
                (_, user) => user)
            .AnyAsync(user =>
                    user.DeletionScheduledAt == null
                    && !(user.LockoutEnabled && user.LockoutEnd != null && user.LockoutEnd > now)
                    && !(user.BanUntil != null && user.BanUntil > now),
                cancellationToken);
    }
}
