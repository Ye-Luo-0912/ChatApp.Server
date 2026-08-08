using System.Data;
using Core.Interfaces.Auth;
using Core.Models.Identity;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.Services.Auth;

/// <summary>
/// Database primitive for all security-version changes. PostgreSQL uses one
/// UPDATE ... RETURNING statement, so concurrent security mutations linearize
/// at the row without a read-modify-write race.
/// </summary>
public sealed class SecurityVersionAdvancer(
    UserDbContext db,
    IAuthSnapshotStore? snapshots = null,
    SecurityVersionInvalidationDispatcher? invalidationDispatcher = null) : ISecurityVersionAdvancer
{
    public async Task<long?> AdvanceSecurityVersionAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
            return null;

        long? next;
        if (IsNpgsql())
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE "AspNetUsers"
                SET "SecurityVersion" = "SecurityVersion" + 1
                WHERE "Id" = @user_id AND "SecurityVersion" < @max_version
                RETURNING "SecurityVersion";
                """;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            AddParameter(command, "user_id", userId);
            AddParameter(command, "max_version", long.MaxValue);

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            next = result is null or DBNull
                ? null
                : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            // InMemory test contexts do not support PostgreSQL RETURNING. Keep
            // the same boundary and semantics for those tests.
            var user = await db.Users.FirstOrDefaultAsync(
                    u => u.Id == userId, cancellationToken)
                .ConfigureAwait(false);
            if (user is null || user.SecurityVersion == long.MaxValue)
                return null;

            var entry = db.Entry(user);
            entry.Property(u => u.SecurityVersion).CurrentValue =
                Math.Max(1, checked(user.SecurityVersion + 1));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            next = entry.Property(u => u.SecurityVersion).CurrentValue;
        }

        if (next is { } version)
        {
            // In case a caller keeps a tracked entity alive, prevent a later
            // SaveChanges from copying its pre-increment value back.
            var tracked = db.ChangeTracker.Entries<ApplicationUser>()
                .FirstOrDefault(e => e.Entity.Id == userId);
            if (tracked is not null)
            {
                var property = tracked.Property(u => u.SecurityVersion);
                property.CurrentValue = version;
                property.OriginalValue = version;
                property.IsModified = false;
            }

            if (db.Database.CurrentTransaction?.GetDbTransaction() is { } transaction
                && invalidationDispatcher is not null)
            {
                // The UPDATE is inside the caller's transaction. Do not
                // evict before commit: another instance could read the old
                // committed row and repopulate the cache while this update is
                // still invisible, leaving a stale snapshot behind.
                invalidationDispatcher.Register(transaction, userId, version);
            }
            else if (snapshots is not null)
            {
                // No explicit transaction exists here, so the atomic UPDATE
                // has committed before ExecuteScalarAsync returned. The
                // direct invalidation is therefore safe. The fallback keeps
                // manually-created/test DbContexts compatible.
                await snapshots.InvalidateAsync(
                        userId,
                        minimumSecurityVersion: version,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return next;
    }

    private static bool IsNpgsql(DbContext db) =>
        db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    private bool IsNpgsql() => IsNpgsql(db);

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        parameter.DbType = value is long ? DbType.Int64 : DbType.Object;
        command.Parameters.Add(parameter);
    }
}
