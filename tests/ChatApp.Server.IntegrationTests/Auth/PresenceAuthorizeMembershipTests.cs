using ChatApp.Server.IntegrationTests.Support;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

/// <summary>
/// Presence 成员鉴权：同会话成员（含非 dm: 群预留 id）应放行。
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PresenceAuthorizeMembershipTests(PostgresTestFixture postgres)
{
    [SkippableFact]
    public async Task SharedGroupMembership_AllowsTarget_WithoutDirectConversationId()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        var cs = postgres.ConnectionString;
        await SeedSharedGroupMembershipAsync(cs, watcher: 1001, target: 1002, conversationId: "group:presence-test-1");

        var allowed = await PresenceMembershipFilter.FilterSharedMembersAsync(
            connectionString: cs,
            schema: "realtime",
            watcherUserId: 1001,
            targetUserIds: [1002, 1003],
            logger: NullLogger.Instance,
            ct: CancellationToken.None);

        Assert.Equal([1002L], allowed);
    }

    private static async Task SeedSharedGroupMembershipAsync(
        string connectionString,
        long watcher,
        long target,
        string conversationId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand(
                         """
                         CREATE SCHEMA IF NOT EXISTS realtime;
                         CREATE TABLE IF NOT EXISTS realtime.conversation_members (
                             conversation_id text NOT NULL,
                             user_id bigint NOT NULL,
                             joined_at_ms bigint NOT NULL DEFAULT 0,
                             PRIMARY KEY (conversation_id, user_id)
                         );
                         ALTER TABLE realtime.conversation_members
                             ADD COLUMN IF NOT EXISTS joined_at_ms bigint NOT NULL DEFAULT 0;
                         """,
                         conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
                         """
                         INSERT INTO realtime.conversation_members (conversation_id, user_id)
                         VALUES (@cid, @watcher), (@cid, @target)
                         ON CONFLICT DO NOTHING;
                         """,
                         conn))
        {
            cmd.Parameters.AddWithValue("cid", conversationId);
            cmd.Parameters.AddWithValue("watcher", watcher);
            cmd.Parameters.AddWithValue("target", target);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
