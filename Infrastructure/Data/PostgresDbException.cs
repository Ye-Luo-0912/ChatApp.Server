using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Data;

/// <summary>
/// PostgreSQL / EF 异常判定辅助。
/// </summary>
internal static class PostgresDbException
{
    public const string EmailOutboxIdempotencyConstraint = "IX_EmailOutbox_IdempotencyKey_Active";
    // 与 Initial 迁移中的唯一索引名一致
    public const string FriendGroupNameConstraint = "IX_T_FriendGroup_UserId_GroupName";

    public static bool IsUniqueViolation(DbUpdateException ex, string expectedConstraintName)
    {
        return ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: { } name
        } && string.Equals(name, expectedConstraintName, StringComparison.Ordinal);
    }
}
