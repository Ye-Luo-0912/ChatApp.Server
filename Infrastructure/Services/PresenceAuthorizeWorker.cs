using ChatApp.Realtime.Integration;
using ChatApp.Realtime.Integration.Ephemeral;
using Core.Interfaces;
using Core.Models.Friend;
using Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Infrastructure.Services;

/// <summary>
/// NATS Core request/reply：Presence 查询目标须为互为好友，或同属任一会话成员（含群预留）。
/// RealtimeIntegration:Url 未配置时不启动。
/// </summary>
public sealed class PresenceAuthorizeWorker(
    IRealtimeMessageBus? bus,
    IServiceScopeFactory scopeFactory,
    IOptions<MessageEvidenceOptions> evidenceOptions,
    IOptions<DataExportStorageOptions> exportOptions,
    ILogger<PresenceAuthorizeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (bus is null)
        {
            logger.LogInformation("RealtimeIntegration:Url 未配置，跳过 PresenceAuthorizeWorker");
            return;
        }

        logger.LogInformation("PresenceAuthorizeWorker 开始服务 chat.presence.authorize");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await bus.ServePresenceAuthorizeAsync(HandleAsync, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "PresenceAuthorizeWorker 异常，将重试");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<PresenceAuthorizeResponse> HandleAsync(
        PresenceAuthorizeQuery query,
        CancellationToken ct)
    {
        if (query.WatcherUserId <= 0 || query.TargetUserIds.Count == 0)
            return new PresenceAuthorizeResponse { AllowedUserIds = [] };

        var targets = query.TargetUserIds
            .Where(static id => id > 0)
            .Distinct()
            .Take(100)
            .ToArray();
        if (targets.Length == 0)
            return new PresenceAuthorizeResponse { AllowedUserIds = [] };

        await using var scope = scopeFactory.CreateAsyncScope();
        var friendship = scope.ServiceProvider.GetRequiredService<IFriendshipService>();
        var allowed = new List<long>(targets.Length);
        List<long>? needMembership = null;

        IReadOnlyDictionary<long, FriendshipStatusInfo> relationships;
        try
        {
            relationships = await friendship
                .CheckRelationshipsAsync(query.WatcherUserId, targets, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "批量关系查询失败，全部降级到成员检查");
            relationships = new Dictionary<long, FriendshipStatusInfo>();
        }

        foreach (var targetId in targets)
        {
            if (targetId == query.WatcherUserId)
                continue;

            if (relationships.TryGetValue(targetId, out var status) && status.IsMutual)
                allowed.Add(targetId);
            else
                (needMembership ??= []).Add(targetId);
        }

        if (needMembership is { Count: > 0 })
        {
            var members = await PresenceMembershipFilter.FilterSharedMembersAsync(
                    ResolveRealtimeConnectionString(),
                    string.IsNullOrWhiteSpace(evidenceOptions.Value.Schema)
                        ? "realtime"
                        : evidenceOptions.Value.Schema.Trim(),
                    query.WatcherUserId,
                    needMembership,
                    logger,
                    ct)
                .ConfigureAwait(false);
            foreach (var id in members)
            {
                if (!allowed.Contains(id))
                    allowed.Add(id);
            }
        }

        return new PresenceAuthorizeResponse { AllowedUserIds = allowed };
    }

    private string? ResolveRealtimeConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(exportOptions.Value.RealtimeConnectionString))
            return exportOptions.Value.RealtimeConnectionString;
        if (!string.IsNullOrWhiteSpace(evidenceOptions.Value.RealtimeConnectionString))
            return evidenceOptions.Value.RealtimeConnectionString;
        return null;
    }
}

/// <summary>
/// Presence 会话成员鉴权：watcher 与 target 同属任一 conversation_members 行即放行（DM/群通用）。
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
        var table = $"\"{safeSchema}\".\"conversation_members\"";
        var targets = targetUserIds
            .Where(id => id > 0 && id != watcherUserId)
            .Distinct()
            .ToArray();
        if (targets.Length == 0)
            return [];

        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

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
