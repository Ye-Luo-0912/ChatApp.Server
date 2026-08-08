using Microsoft.Extensions.Logging;
using Npgsql;

namespace Infrastructure.Services;

/// <summary>
/// Compatibility helper retained for focused integration tests and callers
/// outside the hosted Presence worker. The production worker uses
/// <see cref="PresenceAuthorizationService"/>, which reuses the process-level
/// <see cref="RealtimePostgresDataSource"/>.
/// </summary>
internal static class PresenceMembershipFilter
{
    public static async Task<IReadOnlyList<long>> FilterSharedMembersAsync(
        string? connectionString,
        string schema,
        long watcherUserId,
        IReadOnlyList<long> targetUserIds,
        ILogger logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || targetUserIds.Count == 0)
            return [];

        var safeSchema = string.IsNullOrWhiteSpace(schema) ? "realtime" : schema.Trim();
        var table = $"\"{safeSchema.Replace("\"", "\"\"", StringComparison.Ordinal)}\".\"conversation_members\"";
        var targets = targetUserIds
            .Where(id => id > 0 && id != watcherUserId)
            .Distinct()
            .ToArray();
        if (targets.Length == 0)
            return [];

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(
                $"""
                 SELECT DISTINCT t.user_id
                 FROM {table} w
                 INNER JOIN {table} t
                     ON t.conversation_id = w.conversation_id
                 WHERE w.user_id = @watcher
                   AND t.user_id = ANY(@targets)
                   AND t.user_id <> @watcher;
                 """,
                conn);
            cmd.Parameters.AddWithValue("watcher", watcherUserId);
            cmd.Parameters.AddWithValue("targets", targets);

            var allowed = new List<long>(targets.Length);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                allowed.Add(reader.GetInt64(0));

            return allowed;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Presence 会话成员校验失败 Watcher={Watcher}", watcherUserId);
            return [];
        }
    }
}
