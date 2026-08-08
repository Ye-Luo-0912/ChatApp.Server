using Core.Caching;
using Core.Exceptions;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Models.Presence;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Infrastructure.Services;

/// <summary>
/// Presence 授权投影：
/// <list type="bullet">
/// <item>一次 Server DB UNION 查询同时得到 block 与双向好友关系；</item>
/// <item>共享会话成员查询使用进程级 NpgsqlDataSource；</item>
/// <item>缓存命中时先检查 block deny marker，再批量比较关系/成员 epoch；</item>
/// <item>缓存不可用回权威查询，任一权威查询失败都由调用方 fail closed。</item>
/// </list>
/// </summary>
public sealed class PresenceAuthorizationService(
    UserDbContext db,
    ICacheValueStore cache,
    IAtomicCacheStore atomicCache,
    RealtimePostgresDataSource realtimeDataSource,
    ILogger<PresenceAuthorizationService> logger) : IPresenceAuthorizationService
{
    private const int MaxTargets = 100;

    public async Task<IReadOnlySet<long>> AuthorizeAsync(
        long watcherUserId,
        IReadOnlyList<long> targetUserIds,
        CancellationToken cancellationToken = default)
    {
        // The Server DB parameter is retained for constructor compatibility
        // with the old relationship implementation. Relationship authority
        // now lives in the Realtime data source; do not accidentally re-add a
        // second PostgreSQL round trip here.
        _ = db;

        if (watcherUserId <= 0 || targetUserIds.Count == 0)
            return new HashSet<long>();

        var targets = targetUserIds
            .Where(static id => id > 0)
            .Distinct()
            .Take(MaxTargets)
            .Where(id => id != watcherUserId)
            .ToArray();
        if (targets.Length == 0)
            return new HashSet<long>();

        var allowed = new HashSet<long>();
        var misses = new List<long>(targets.Length);
        var cacheRead = await TryReadProjectionAsync(
                watcherUserId,
                targets,
                allowed,
                misses,
                cancellationToken)
            .ConfigureAwait(false);
        if (!cacheRead.Success)
        {
            misses.Clear();
            misses.AddRange(targets);
            allowed.Clear();
        }

        if (misses.Count == 0)
            return allowed;

        var projection = await LoadAuthoritativeProjectionAsync(
                watcherUserId,
                misses,
                cacheRead.Epochs,
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var targetId in misses)
        {
            if (projection.BlockedTargets.Contains(targetId))
                continue;

            var isFriend = projection.MutualFriendTargets.Contains(targetId);
            var isSharedMember = projection.SharedMemberTargets.Contains(targetId);
            if (isFriend || isSharedMember)
                allowed.Add(targetId);

            projection.CacheWrites.Add(new CacheSetRequest
            {
                Key = PresenceAuthorizationCache.DecisionKey(watcherUserId, targetId),
                Value = new PresenceAuthorizationProjection
                {
                    Allowed = isFriend || isSharedMember,
                    RelationshipEpoch = projection.Epochs.RelationshipEpochByTarget
                        .GetValueOrDefault(targetId),
                    WatcherMembershipEpoch = projection.Epochs.WatcherMembershipEpoch,
                    TargetMembershipEpoch = projection.Epochs.TargetMembershipEpochByTarget
                        .GetValueOrDefault(targetId),
                    MembershipDependent = !isFriend,
                },
                Expiration = PresenceAuthorizationCache.DecisionTtl,
            });
        }

        foreach (var blockedTarget in projection.BlockedTargets)
        {
            if (!misses.Contains(blockedTarget))
                continue;

            projection.CacheWrites.Add(new CacheSetRequest
            {
                Key = PresenceAuthorizationCache.BlockKey(watcherUserId, blockedTarget),
                Value = new PresenceAuthorizationProjection
                {
                    IsBlockDenyMarker = true,
                    Allowed = false,
                    RelationshipEpoch = projection.Epochs.RelationshipEpochByTarget
                        .GetValueOrDefault(blockedTarget),
                },
                Expiration = PresenceAuthorizationCache.BlockMarkerTtl,
            });
        }

        await SetProjectionBestEffortAsync(projection.CacheWrites, cancellationToken)
            .ConfigureAwait(false);
        return allowed;
    }

    private async Task<(bool Success, EpochSnapshot Epochs)> TryReadProjectionAsync(
        long watcherUserId,
        IReadOnlyList<long> targets,
        HashSet<long> allowed,
        List<long> misses,
        CancellationToken cancellationToken)
    {
        var keys = new string[targets.Count * 2];
        for (var i = 0; i < targets.Count; i++)
        {
            keys[i] = PresenceAuthorizationCache.BlockKey(watcherUserId, targets[i]);
            keys[targets.Count + i] =
                PresenceAuthorizationCache.DecisionKey(watcherUserId, targets[i]);
        }

        var epochKeys = new string[1 + targets.Count * 2];
        epochKeys[0] = PresenceAuthorizationCache.MembershipEpochKey(watcherUserId);
        for (var i = 0; i < targets.Count; i++)
        {
            epochKeys[1 + i] =
                PresenceAuthorizationCache.RelationshipEpochKey(watcherUserId, targets[i]);
            epochKeys[1 + targets.Count + i] =
                PresenceAuthorizationCache.MembershipEpochKey(targets[i]);
        }

        IReadOnlyList<PresenceAuthorizationProjection?> values;
        IReadOnlyList<long> epochValues;
        try
        {
            var valuesTask = cache.GetManyAsync<PresenceAuthorizationProjection>(keys, cancellationToken);
            var epochTask = cache.GetManyAsync<long>(epochKeys, cancellationToken);
            await Task.WhenAll(valuesTask, epochTask).ConfigureAwait(false);
            values = await valuesTask.ConfigureAwait(false);
            epochValues = await epochTask.ConfigureAwait(false);
        }
        catch (CacheUnavailableException ex)
        {
            logger.LogDebug(ex, "Presence 授权缓存不可用，回退权威查询");
            return (false, EpochSnapshot.Empty);
        }
        catch (CacheCorruptedException ex)
        {
            logger.LogWarning(ex, "Presence 授权缓存损坏，回退权威查询");
            return (false, EpochSnapshot.Empty);
        }
        catch (CacheSerializationException ex)
        {
            logger.LogWarning(ex, "Presence 授权缓存序列化失败，回退权威查询");
            return (false, EpochSnapshot.Empty);
        }

        if (values.Count != keys.Length || epochValues.Count != epochKeys.Length)
            return (false, EpochSnapshot.Empty);

        var epochs = new EpochSnapshot(
            ToNullableEpoch(epochValues[0]),
            targets.Select((targetId, index) =>
                    (targetId, epoch: ToNullableEpoch(epochValues[1 + index])))
                .ToDictionary(x => x.targetId, x => x.epoch),
            targets.Select((targetId, index) =>
                    (targetId, epoch: ToNullableEpoch(epochValues[1 + targets.Count + index])))
                .ToDictionary(x => x.targetId, x => x.epoch));

        for (var i = 0; i < targets.Count; i++)
        {
            var deny = values[i];
            var decision = values[targets.Count + i];
            var targetId = targets[i];
            var relationshipEpoch = epochs.RelationshipEpochByTarget.GetValueOrDefault(targetId);
            if (deny?.IsBlockDenyMarker == true)
            {
                if (relationshipEpoch is { } current
                    && current > 0
                    && deny.RelationshipEpoch == current)
                    continue;

                misses.Add(targetId);
                continue;
            }

            if (decision is null)
            {
                misses.Add(targetId);
                continue;
            }

            if (relationshipEpoch is not { } currentRelationship
                || currentRelationship <= 0
                || decision.RelationshipEpoch != currentRelationship)
            {
                misses.Add(targetId);
                continue;
            }

            if (decision.MembershipDependent
                && (epochs.WatcherMembershipEpoch is not { } watcherMembershipEpoch
                    || watcherMembershipEpoch <= 0
                    || epochs.TargetMembershipEpochByTarget.GetValueOrDefault(targetId)
                        is not { } targetMembershipEpoch
                    || targetMembershipEpoch <= 0
                    || decision.WatcherMembershipEpoch != watcherMembershipEpoch
                    || decision.TargetMembershipEpoch != targetMembershipEpoch))
            {
                misses.Add(targetId);
                continue;
            }

            if (decision.Allowed)
                allowed.Add(targetId);
        }

        return (true, epochs);
    }

    private async Task<AuthoritativeProjection> LoadAuthoritativeProjectionAsync(
        long watcherUserId,
        IReadOnlyList<long> targets,
        EpochSnapshot cacheEpochs,
        CancellationToken cancellationToken)
    {
        // Block and friendship state remains authoritative in the Server DB.
        // Do not let a shared conversation bypass a Server-side block.  The
        // two projections intentionally have the same shape so EF can emit a
        // single UNION ALL query instead of one round trip per target.
        var relationshipRows = await db.BlockRecords
            .AsNoTracking()
            .Where(b => (b.BlockerId == watcherUserId && targets.Contains(b.BlockedUserId))
                        || (b.BlockedUserId == watcherUserId && targets.Contains(b.BlockerId)))
            .Select(b => new
            {
                TargetId = b.BlockerId == watcherUserId ? b.BlockedUserId : b.BlockerId,
                Kind = 0,
                IsActive = true,
            })
            .Concat(db.Friendships
                .IgnoreQueryFilters()
                .Where(f => (f.UserId == watcherUserId && targets.Contains(f.FriendId))
                            || (f.FriendId == watcherUserId && targets.Contains(f.UserId)))
                .Select(f => new
                {
                    TargetId = f.UserId == watcherUserId ? f.FriendId : f.UserId,
                    // 1 = watcher -> target, 2 = target -> watcher.
                    Kind = f.UserId == watcherUserId ? 1 : 2,
                    IsActive = !f.IsDeleted,
                }))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var blocked = relationshipRows
            .Where(static row => row.Kind == 0)
            .Select(static row => row.TargetId)
            .ToHashSet();
        var mutual = relationshipRows
            .Where(static row => row.Kind == 1 && row.IsActive)
            .Select(static row => row.TargetId)
            .Intersect(
                relationshipRows
                    .Where(static row => row.Kind == 2 && row.IsActive)
                    .Select(static row => row.TargetId))
            .ToHashSet();

        // A mutual friendship is already sufficient.  Avoid a second
        // PostgreSQL data source query for those targets and never query
        // Realtime membership for a blocked pair.
        var needMembership = targets
            .Where(targetId => !blocked.Contains(targetId) && !mutual.Contains(targetId))
            .ToArray();
        var sharedMembers = await LoadSharedMembersAsync(
                watcherUserId,
                needMembership,
                cancellationToken)
            .ConfigureAwait(false);

        var epochs = await ResolveEpochsAsync(
                watcherUserId,
                targets,
                cacheEpochs,
                cancellationToken)
            .ConfigureAwait(false);

        return new AuthoritativeProjection(
            blocked,
            mutual,
            sharedMembers,
            epochs,
            new List<CacheSetRequest>(targets.Count * 2));
    }


    private async Task<HashSet<long>> LoadSharedMembersAsync(
        long watcherUserId,
        IReadOnlyList<long> targets,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<long>();
        var dataSource = realtimeDataSource.DataSource;
        if (dataSource is null || targets.Count == 0)
            return result;

        var table = $"{QuoteIdentifier(realtimeDataSource.Schema)}.\"conversation_members\"";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
             SELECT DISTINCT t.user_id
             FROM {table} w
             INNER JOIN {table} t
                 ON t.conversation_id = w.conversation_id
             WHERE w.user_id = @watcher
               AND t.user_id = ANY(@targets)
               AND t.user_id <> @watcher
             """,
            connection);
        command.Parameters.AddWithValue("watcher", watcherUserId);
        command.Parameters.AddWithValue("targets", targets.ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var targetId = reader.GetInt64(0);
            result.Add(targetId);
        }

        return result;
    }

    private async Task<ResolvedEpochs> ResolveEpochsAsync(
        long watcherUserId,
        IReadOnlyList<long> targets,
        EpochSnapshot cacheEpochs,
        CancellationToken cancellationToken)
    {
        var watcherMembershipEpoch = cacheEpochs.WatcherMembershipEpoch.GetValueOrDefault();
        var relationshipEpochs = new Dictionary<long, long>(targets.Count);
        var targetMembershipEpochs = new Dictionary<long, long>(targets.Count);
        var allEpochsPresent = watcherMembershipEpoch > 0;
        foreach (var targetId in targets)
        {
            var relationshipEpoch = cacheEpochs.RelationshipEpochByTarget
                .GetValueOrDefault(targetId).GetValueOrDefault();
            var targetMembershipEpoch = cacheEpochs.TargetMembershipEpochByTarget
                .GetValueOrDefault(targetId).GetValueOrDefault();
            relationshipEpochs[targetId] = relationshipEpoch;
            targetMembershipEpochs[targetId] = targetMembershipEpoch;
            allEpochsPresent &= relationshipEpoch > 0 && targetMembershipEpoch > 0;
        }

        if (!allEpochsPresent)
        {
            try
            {
                var keys = new string[1 + targets.Count * 2];
                keys[0] = PresenceAuthorizationCache.MembershipEpochKey(watcherUserId);
                for (var i = 0; i < targets.Count; i++)
                {
                    keys[1 + i] = PresenceAuthorizationCache.RelationshipEpochKey(
                        watcherUserId, targets[i]);
                    keys[1 + targets.Count + i] = PresenceAuthorizationCache.MembershipEpochKey(
                        targets[i]);
                }

                var values = await PresenceAuthorizationCache.EnsureEpochsAsync(
                        cache,
                        atomicCache,
                        keys,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (values.Length == keys.Length)
                {
                    watcherMembershipEpoch = values[0];
                    for (var i = 0; i < targets.Count; i++)
                    {
                        relationshipEpochs[targets[i]] = values[1 + i];
                        targetMembershipEpochs[targets[i]] =
                            values[1 + targets.Count + i];
                    }
                }
            }
            catch (CacheUnavailableException ex)
            {
                logger.LogDebug(ex, "Presence epoch 批量初始化失败，跳过投影写入");
            }
            catch (CacheSerializationException ex)
            {
                logger.LogWarning(ex, "Presence epoch 批量初始化序列化失败");
            }
        }

        return new ResolvedEpochs(
            watcherMembershipEpoch,
            relationshipEpochs,
            targetMembershipEpochs);
    }

    private async Task SetProjectionBestEffortAsync(
        IReadOnlyList<CacheSetRequest> writes,
        CancellationToken cancellationToken)
    {
        if (writes.Count == 0)
            return;

        try
        {
            await atomicCache.SetManyAsync(writes, cancellationToken).ConfigureAwait(false);
        }
        catch (CacheUnavailableException ex)
        {
            logger.LogDebug(ex, "Presence 授权投影写入失败");
        }
        catch (CacheSerializationException ex)
        {
            logger.LogWarning(ex, "Presence 授权投影序列化失败");
        }
    }

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static long? ToNullableEpoch(long value) => value > 0 ? value : null;


    private sealed record AuthoritativeProjection(
        HashSet<long> BlockedTargets,
        HashSet<long> MutualFriendTargets,
        HashSet<long> SharedMemberTargets,
        ResolvedEpochs Epochs,
        List<CacheSetRequest> CacheWrites);

    private sealed record EpochSnapshot(
        long? WatcherMembershipEpoch,
        Dictionary<long, long?> RelationshipEpochByTarget,
        Dictionary<long, long?> TargetMembershipEpochByTarget)
    {
        public static EpochSnapshot Empty { get; } = new(
            null,
            new Dictionary<long, long?>(),
            new Dictionary<long, long?>());
    }

    private sealed record ResolvedEpochs(
        long WatcherMembershipEpoch,
        Dictionary<long, long> RelationshipEpochByTarget,
        Dictionary<long, long> TargetMembershipEpochByTarget);
}
