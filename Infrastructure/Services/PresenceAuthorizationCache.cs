using Core.Interfaces.Cache;
using Core.Models.Presence;
using System.Globalization;

namespace Infrastructure.Services;

/// <summary>Presence 授权投影与 block deny marker 的专用缓存键/失效操作。</summary>
internal static class PresenceAuthorizationCache
{
    private const string DecisionPrefix = "presence:authorization:v2:";
    private const string BlockPrefix = "presence:block-deny:v2:";
    private const string RelationshipEpochPrefix = "presence:relationship-epoch:v1:";
    private const string MembershipEpochPrefix = "presence:membership-epoch:v1:";

    public static readonly TimeSpan DecisionTtl = TimeSpan.FromSeconds(1);
    // Keep a missed unblock bounded as well as a missed block. A stale deny
    // is safe but should not make a user invisible for minutes.
    public static readonly TimeSpan BlockMarkerTtl = TimeSpan.FromSeconds(1);
    // Epoch keys outlive decision entries. A missing epoch is treated as a
    // cache miss and never as a valid version.
    public static readonly TimeSpan EpochTtl = TimeSpan.FromHours(24);

    private const string EnsureEpochsScript = """
        local result = {}
        for index, key in ipairs(KEYS) do
          local current = tonumber(redis.call('GET', key) or '0') or 0
          if current <= 0 then
            redis.call('SET', key, '1', 'PX', ARGV[1], 'NX')
            current = tonumber(redis.call('GET', key) or '0') or 0
          end
          result[index] = current
        end
        return result
        """;

    public static string DecisionKey(long watcherUserId, long targetUserId) =>
        $"{DecisionPrefix}{watcherUserId}:{targetUserId}";

    public static string RelationshipEpochKey(long userId1, long userId2)
    {
        var low = Math.Min(userId1, userId2);
        var high = Math.Max(userId1, userId2);
        return $"{RelationshipEpochPrefix}{low}:{high}";
    }

    public static string MembershipEpochKey(long userId) =>
        $"{MembershipEpochPrefix}{userId}";

    /// <summary>
    /// 读取当前 epoch；首次使用时只做一次 NX 初始化。
    /// 初始化与关系变更后的 INCR 竞争时，NX 失败的一方重新读取，
    /// 因此不会把已推进的版本覆盖回旧值。
    /// </summary>
    public static async Task<long?> EnsureEpochAsync(
        ICacheValueStore values,
        IAtomicCacheStore atomic,
        string key,
        CancellationToken cancellationToken)
    {
        var raw = await values.StringGetAsync(key, cancellationToken).ConfigureAwait(false);
        if (TryParseEpoch(raw, out var current))
            return current;

        if (raw is not null)
            return null;

        var created = await atomic.StringSetIfNotExistsAsync(
                key,
                "1",
                EpochTtl,
                cancellationToken)
            .ConfigureAwait(false);
        if (created)
            return 1;

        raw = await values.StringGetAsync(key, cancellationToken).ConfigureAwait(false);
        return TryParseEpoch(raw, out current) ? current : null;
    }

    /// <summary>
    /// Ensures a batch of relationship/membership epochs with one Lua round
    /// trip. The fallback keeps in-memory and older cache test doubles
    /// compatible; a missing/invalid value is never treated as a valid epoch.
    /// </summary>
    public static async Task<long[]> EnsureEpochsAsync(
        ICacheValueStore values,
        IAtomicCacheStore atomic,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
            return [];

        var result = await atomic.EvaluateScriptAsync(
                EnsureEpochsScript,
                keys,
                [Math.Max(1, (long)EpochTtl.TotalMilliseconds)
                    .ToString(CultureInfo.InvariantCulture)],
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Length == keys.Count && result.All(static value => value > 0))
            return result;

        // A non-Redis implementation may not support EVAL. It is still safe
        // to initialize each missing key through the original NX primitive.
        var fallback = new long[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            fallback[i] = (await EnsureEpochAsync(
                    values, atomic, keys[i], cancellationToken)
                .ConfigureAwait(false)).GetValueOrDefault();
        return fallback;
    }

    public static Task<long> AdvanceRelationshipEpochAsync(
        IAtomicCacheStore atomic,
        long userId1,
        long userId2,
        CancellationToken cancellationToken) =>
        atomic.StringIncrementAsync(
            RelationshipEpochKey(userId1, userId2),
            EpochTtl,
            cancellationToken);

    public static Task<long> AdvanceMembershipEpochAsync(
        IAtomicCacheStore atomic,
        long userId,
        CancellationToken cancellationToken) =>
        atomic.StringIncrementAsync(
            MembershipEpochKey(userId),
            EpochTtl,
            cancellationToken);

    public static string BlockKey(long userId1, long userId2)
    {
        var low = Math.Min(userId1, userId2);
        var high = Math.Max(userId1, userId2);
        return $"{BlockPrefix}{low}:{high}";
    }

    /// <summary>
    /// 在数据库事务提交后调用。block=true 先写 deny marker，再删除正向结果；
    /// block=false 同时删除 marker 与正向结果；未知状态只删除正向结果。
    /// </summary>
    public static async Task InvalidatePairAsync(
        ICacheValueStore cache,
        IAtomicCacheStore? atomicCache,
        long userId1,
        long userId2,
        bool? blocked,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (userId1 <= 0 || userId2 <= 0 || userId1 == userId2)
            return;

        long? relationshipEpoch = null;
        if (atomicCache is not null)
        {
            try
            {
                relationshipEpoch = await AdvanceRelationshipEpochAsync(
                        atomicCache,
                        userId1,
                        userId2,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Projection deletion below remains useful if the epoch
                // advance is temporarily unavailable. A zero marker is
                // deliberately never considered a valid version.
            }
        }

        var decisionKeys = new[]
        {
            DecisionKey(userId1, userId2),
            DecisionKey(userId2, userId1),
        };

        if (blocked is true)
        {
            // A deny marker is safe to leave behind if the following delete is
            // ambiguous: it can only cause a temporary false negative.
            try
            {
                await cache.SetAsync(
                        BlockKey(userId1, userId2),
                        new PresenceAuthorizationProjection
                        {
                            IsBlockDenyMarker = true,
                            Allowed = false,
                            RelationshipEpoch = relationshipEpoch ?? 0,
                        },
                        BlockMarkerTtl,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                await cache.RemoveManyAsync(decisionKeys, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (blocked is false)
        {
            await cache.RemoveManyAsync(
                    [BlockKey(userId1, userId2), decisionKeys[0], decisionKeys[1]],
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await cache.RemoveManyAsync(decisionKeys, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParseEpoch(string? raw, out long epoch)
    {
        return long.TryParse(
                   raw,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out epoch)
               && epoch > 0;
    }
}
