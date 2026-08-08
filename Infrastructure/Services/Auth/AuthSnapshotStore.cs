using System.Text;
using Core.Exceptions;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Interfaces.Cache;
using Core.Models.Auth;
using Core.Settings;
using Infrastructure.Auth;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services.Auth;

/// <summary>
/// Cache-aside store for the authorization fence. Database access is confined to
/// this owner instead of being hidden in the generic cache abstraction.
/// </summary>
public sealed class AuthSnapshotStore(
    UserDbContext db,
    ICacheValueStore cache,
    AuthSnapshotL1Cache l1,
    IOptions<JwtSettings> options,
    ILogger<AuthSnapshotStore> logger,
    IAuthSnapshotL1InvalidationBus? invalidationBus = null,
    IAtomicCacheStore? atomicCache = null,
    ISerializer? serializer = null) : IAuthSnapshotStore
{
    private const string KeyPrefix = "auth:fence:v2:";
    private const string VersionKeyPrefix = "auth:fence:version:v1:";
    private const string VersionedSetScript = """
        local candidate = tonumber(ARGV[1])
        local current = tonumber(redis.call('GET', KEYS[2]) or '0') or 0
        if current == 0 then
          local payload = redis.call('GET', KEYS[1]) or ''
          local encoded = string.match(payload, '"securityVersion"%s*:%s*(%d+)')
          current = tonumber(encoded) or 0
        end
        if candidate < current then
          return {0}
        end
        redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3])
        redis.call('SET', KEYS[2], ARGV[1], 'PX', ARGV[4])
        return {1}
        """;
    private const string InvalidateVersionScript = """
        local requested = tonumber(ARGV[1]) or 0
        local current = tonumber(redis.call('GET', KEYS[2]) or '0') or 0
        if requested > current then
          redis.call('SET', KEYS[2], ARGV[1], 'PX', ARGV[2])
        end
        redis.call('DEL', KEYS[1])
        return {1}
        """;
    private readonly TimeSpan _distributedTtl = TimeSpan.FromSeconds(
        Math.Max(1, options.Value.AuthFenceDistributedTtlSeconds));
    private readonly TimeSpan _versionFloorTtl = TimeSpan.FromMinutes(
        Math.Max(5, (int)options.Value.AccessTokenExpirationMinutes));
    private readonly IAuthSnapshotL1InvalidationBus? _invalidationBus = invalidationBus;
    private readonly IAtomicCacheStore? _atomicCache = atomicCache;
    private readonly ISerializer? _serializer = serializer;

    public async Task<UserAuthSnapshot?> GetAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
            return null;

        if (l1.TryGet(userId, out var local))
        {
            if (local!.ClaimsLoaded)
                return local;

            return await LoadRolesAndCacheAsync(local, cancellationToken)
                .ConfigureAwait(false);
        }

        var distributed = await TryGetDistributedAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        if (IsValid(distributed, userId, DateTimeOffset.UtcNow))
        {
            if (distributed!.ClaimsLoaded)
            {
                l1.Set(distributed);
                return distributed;
            }

            return await LoadRolesAndCacheAsync(distributed, cancellationToken)
                .ConfigureAwait(false);
        }

        var authoritative = await GetAuthoritativeAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        if (authoritative is null)
            return null;

        l1.Set(authoritative);
        await SetDistributedBestEffortAsync(authoritative, cancellationToken)
            .ConfigureAwait(false);
        return authoritative;
    }

    public async Task<UserAuthSnapshot?> GetFenceAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
            return null;

        if (l1.TryGet(userId, out var local))
        {
            if (local!.ClaimsLoaded)
                return local;

            return await LoadRolesAndCacheAsync(local, cancellationToken)
                .ConfigureAwait(false);
        }

        var distributed = await TryGetDistributedAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        if (IsValid(distributed, userId, DateTimeOffset.UtcNow)
            && distributed!.ClaimsLoaded)
        {
            l1.Set(distributed);
            return distributed;
        }

        // The first authentication request for a user pays the authoritative
        // snapshot load and role projection once. The resulting complete
        // snapshot is then kept in L1/Garnet, so warmed requests do not query
        // PostgreSQL and do not need the AT to carry repeated claims.
        var authoritative = await LoadFenceAuthoritativeAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        if (authoritative is null)
            return null;

        return await LoadRolesAndCacheAsync(authoritative, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<UserAuthSnapshot?> GetAuthoritativeAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadFenceAuthoritativeAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
            return null;

        return await LoadRolesAndCacheAsync(
                snapshot,
                cancellationToken,
                cacheResult: false)
            .ConfigureAwait(false);
    }

    private async Task<UserAuthSnapshot?> LoadFenceAuthoritativeAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        AuthSecurityMetrics.RecordAuthFenceRemote("postgres_authoritative");
        var snapshot = await db.Users.AsNoTracking()
            .TagWith("auth-fence")
            .Where(u => u.Id == userId)
            .Select(u => new UserAuthSnapshot
            {
                UserId = u.Id,
                UserName = u.UserName,
                SecurityVersion = u.SecurityVersion,
                AccountState = u.AccountState,
                LockoutEnabled = u.LockoutEnabled,
                LockoutUntil = u.LockoutEnd,
                BanUntil = u.BanUntil,
                DeletionScheduledAt = u.DeletionScheduledAt,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is not null)
            EnsureCacheExpiry(snapshot);

        return snapshot;
    }

    private async Task<UserAuthSnapshot> LoadRolesAndCacheAsync(
        UserAuthSnapshot snapshot,
        CancellationToken cancellationToken,
        bool cacheResult = true)
    {
        AuthSecurityMetrics.RecordAuthFenceRemote("postgres_roles");
        var roles = await db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == snapshot.UserId)
            .Join(db.Roles, ur => ur.RoleId, role => role.Id, (_, role) => role.Name!)
            .Where(name => name != null)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var full = new UserAuthSnapshot
        {
            UserId = snapshot.UserId,
            UserName = snapshot.UserName,
            SecurityVersion = snapshot.SecurityVersion,
            AccountState = snapshot.AccountState,
            Roles = roles,
            RolesLoaded = true,
            LockoutEnabled = snapshot.LockoutEnabled,
            LockoutUntil = snapshot.LockoutUntil,
            BanUntil = snapshot.BanUntil,
            DeletionScheduledAt = snapshot.DeletionScheduledAt,
            ExpiresAt = snapshot.ExpiresAt,
        };

        if (full.UserName is null)
        {
            full.UserName = await db.Users.AsNoTracking()
                .Where(user => user.Id == snapshot.UserId)
                .Select(user => user.UserName)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (cacheResult)
        {
            l1.Set(full);
            await SetDistributedBestEffortAsync(full, cancellationToken)
                .ConfigureAwait(false);
        }

        return full;
    }

    public async Task SetAsync(
        UserAuthSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsValid(snapshot, snapshot.UserId, DateTimeOffset.UtcNow))
            return;

        EnsureCacheExpiry(snapshot);

        l1.Set(snapshot);
        await SetDistributedBestEffortAsync(snapshot, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task InvalidateAsync(
        long userId,
        long? minimumSecurityVersion = null,
        bool failOnCacheError = false,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
            return;

        if (minimumSecurityVersion is not > 0)
        {
            // Keep direct/test callers safe as well: an invalidation without
            // the version returned by the atomic UPDATE obtains the current
            // durable floor before removing derived state.
            minimumSecurityVersion = await db.Users.AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => (long?)user.SecurityVersion)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        l1.Evict(userId, minimumSecurityVersion);
        var publish = true;
        try
        {
            if (minimumSecurityVersion is > 0
                && _atomicCache is not null)
            {
                await _atomicCache.EvaluateScriptAsync(
                        InvalidateVersionScript,
                        [Key(userId), VersionKey(userId)],
                        [
                            minimumSecurityVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ToMilliseconds(_versionFloorTtl).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                if (minimumSecurityVersion is > 0)
                {
                    // This fallback cannot make the two commands fully
                    // atomic, but writing the floor before deleting the value
                    // still makes delayed writers converge on healthy Redis.
                    await cache.StringSetAsync(
                            VersionKey(userId),
                            minimumSecurityVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            _versionFloorTtl,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await cache.RemoveAsync(Key(userId), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            publish = false;
            throw;
        }
        catch (CacheUnavailableException ex)
        {
            if (failOnCacheError)
                throw;

            // The local TTL and the durable SecurityVersion remain the final
            // safety net when a derived-cache delete is unavailable. Do not
            // publish an eviction that would make another instance evict its
            // good L1 and immediately repopulate it from a stale Garnet value.
            publish = false;
            logger.LogDebug(ex, "认证 fence 缓存失效失败 UserId={UserId}", userId);
        }
        catch (CacheSerializationException ex)
        {
            if (failOnCacheError)
                throw;

            publish = false;
            logger.LogWarning(ex, "认证 fence 缓存失效序列化失败 UserId={UserId}", userId);
        }
        catch (CacheCorruptedException ex)
        {
            if (failOnCacheError)
                throw;

            publish = false;
            logger.LogWarning(ex, "认证 fence 缓存失效数据损坏 UserId={UserId}", userId);
        }

        if (publish)
            _invalidationBus?.Publish(userId, minimumSecurityVersion);
    }

    private async Task<UserAuthSnapshot?> TryGetDistributedAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        try
        {
            AuthSecurityMetrics.RecordAuthFenceRemote("garnet_read");
            var distributed = await cache.GetAsync<UserAuthSnapshot>(
                    Key(userId), cancellationToken)
                .ConfigureAwait(false);
            if (distributed is not null)
            {
                // The version floor is normally enforced by the atomic
                // writer, but reads must also reject a stale payload when a
                // legacy/non-atomic cache path or a delayed write survives a
                // missed invalidation. This extra read only occurs after the
                // process-local L1 miss; warmed requests remain zero-remote.
                var floorRaw = await cache.StringGetAsync(
                        VersionKey(userId), cancellationToken)
                    .ConfigureAwait(false);
                if (long.TryParse(
                        floorRaw,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var minimumVersion)
                    && minimumVersion > 0
                    && distributed.SecurityVersion < minimumVersion)
                {
                    AuthSecurityMetrics.RecordAuthFenceRemote("garnet_floor_reject");
                    return null;
                }
            }
            AuthSecurityMetrics.RecordAuthFenceRemote(
                IsValid(distributed, userId, DateTimeOffset.UtcNow)
                    ? "garnet_hit"
                    : "garnet_miss");
            return distributed;
        }
        catch (CacheUnavailableException ex)
        {
            // Security state is fail-closed at the handler boundary; the
            // authoritative query below is the explicit fallback.
            AuthSecurityMetrics.RecordAuthFenceRemote("garnet_error");
            logger.LogDebug(ex, "认证 fence 缓存不可用，回退数据库 UserId={UserId}", userId);
        }
        catch (CacheCorruptedException ex)
        {
            AuthSecurityMetrics.RecordAuthFenceRemote("garnet_error");
            logger.LogWarning(ex, "认证 fence 缓存损坏，回退数据库 UserId={UserId}", userId);
        }
        catch (CacheSerializationException ex)
        {
            AuthSecurityMetrics.RecordAuthFenceRemote("garnet_error");
            logger.LogWarning(ex, "认证 fence 缓存无法反序列化，回退数据库 UserId={UserId}", userId);
        }

        return null;
    }

    private async Task SetDistributedBestEffortAsync(
        UserAuthSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureCacheExpiry(snapshot);
            if (_atomicCache is not null && _serializer is not null)
            {
                var payload = Encoding.UTF8.GetString(_serializer.Serialize(snapshot));
                var result = await _atomicCache.EvaluateScriptAsync(
                        VersionedSetScript,
                        [Key(snapshot.UserId), VersionKey(snapshot.UserId)],
                        [
                            snapshot.SecurityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            payload,
                            ToMilliseconds(_distributedTtl).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ToMilliseconds(_versionFloorTtl).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);

                // Redis returns {0} for a stale writer. An empty response is
                // reserved for non-Redis test doubles/legacy implementations.
                if (result.Length > 0)
                    return;
            }

            await cache.SetAsync(
                    Key(snapshot.UserId), snapshot, _distributedTtl, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CacheUnavailableException ex)
        {
            logger.LogDebug(ex, "认证 fence 缓存写入失败 UserId={UserId}", snapshot.UserId);
        }
        catch (CacheSerializationException ex)
        {
            logger.LogWarning(ex, "认证 fence 缓存序列化失败 UserId={UserId}", snapshot.UserId);
        }
        catch (CacheCorruptedException ex)
        {
            logger.LogWarning(ex, "认证 fence 缓存数据损坏 UserId={UserId}", snapshot.UserId);
        }
    }

    private bool IsValid(
        UserAuthSnapshot? snapshot,
        long userId,
        DateTimeOffset now) =>
        snapshot is not null
        && snapshot.UserId == userId
        && snapshot.SecurityVersion > 0
        && !snapshot.IsExpiredAt(now);

    private void EnsureCacheExpiry(UserAuthSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        if (snapshot.ExpiresAt == default || snapshot.ExpiresAt <= now)
            snapshot.ExpiresAt = now.Add(_distributedTtl);
    }

    private static string Key(long userId) => $"{KeyPrefix}{userId}";

    private static string VersionKey(long userId) => $"{VersionKeyPrefix}{userId}";

    private static long ToMilliseconds(TimeSpan value) =>
        Math.Max(1, (long)value.TotalMilliseconds);
}
