using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Data;

/// <summary>
/// PostgreSQL / EF 异常判定辅助。
/// </summary>
internal static class PostgresDbException
{
    public const string EmailOutboxIdempotencyConstraint = "IX_EmailOutbox_IdempotencyKey_Active";
    public const string FriendGroupNameConstraint = "FriendGroupNameConstraint";
    public const string UserReportDedupeConstraint = "UX_UserReport_DedupeKey";
    public static bool IsUniqueViolation(DbUpdateException ex, string expectedConstraintName)
    {
        return ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: { } name
        } && string.Equals(name, expectedConstraintName, StringComparison.Ordinal);
    }
}
