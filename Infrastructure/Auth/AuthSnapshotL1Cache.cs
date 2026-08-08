using System.Collections.Concurrent;
using Core.Models.Auth;
using Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Infrastructure.Auth;

/// <summary>
/// Per-process L1 for the user authentication fence.
/// It is deliberately short lived: Garnet provides cross-instance state while
/// the short TTL bounds the effect of a missed invalidation notification.
/// </summary>
public sealed class AuthSnapshotL1Cache : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<long, long> _minimumVersions = new();
    private readonly object _writeGate = new();

    public AuthSnapshotL1Cache(int maxEntries, int ttlMilliseconds)
    {
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = Math.Max(1, maxEntries),
            CompactionPercentage = 0.10,
            ExpirationScanFrequency = TimeSpan.FromSeconds(1),
        });
        _ttl = TimeSpan.FromMilliseconds(Math.Max(1, ttlMilliseconds));
    }

    public bool TryGet(long userId, out UserAuthSnapshot? snapshot)
    {
        if (_minimumVersions.TryGetValue(userId, out var minimumVersion))
        {
            if (!_cache.TryGetValue(userId, out var versionedValue)
                || versionedValue is not UserAuthSnapshot versionedSnapshot
                || versionedSnapshot.SecurityVersion < minimumVersion)
            {
                _cache.Remove(userId);
                snapshot = null;
                AuthSecurityMetrics.RecordAuthFenceL1("miss");
                return false;
            }
        }

        if (_cache.TryGetValue(userId, out var value) && value is UserAuthSnapshot found)
        {
            if (found.ExpiresAt != default && found.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _cache.Remove(userId);
                snapshot = null;
                AuthSecurityMetrics.RecordAuthFenceL1("miss");
                return false;
            }

            snapshot = found;
            AuthSecurityMetrics.RecordAuthFenceL1("hit");
            return true;
        }

        snapshot = null;
        AuthSecurityMetrics.RecordAuthFenceL1("miss");
        return false;
    }

    public void Set(UserAuthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_writeGate)
        {
            if (_minimumVersions.TryGetValue(snapshot.UserId, out var minimumVersion)
                && snapshot.SecurityVersion < minimumVersion)
            {
                return;
            }

            if (_cache.TryGetValue(snapshot.UserId, out var current)
                && current is UserAuthSnapshot currentSnapshot
                && currentSnapshot.SecurityVersion > snapshot.SecurityVersion)
            {
                return;
            }

            _cache.Set(snapshot.UserId, snapshot, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _ttl,
                Size = 1,
            });

            // Once a snapshot at or above the invalidation floor is installed,
            // the current entry itself fences delayed writers. Removing the
            // process-local floor avoids retaining one entry per security event.
            if (_minimumVersions.TryGetValue(snapshot.UserId, out minimumVersion)
                && snapshot.SecurityVersion >= minimumVersion)
            {
                _minimumVersions.TryRemove(snapshot.UserId, out _);
            }
        }
    }

    public void Evict(long userId, long? minimumSecurityVersion = null)
    {
        lock (_writeGate)
        {
            if (minimumSecurityVersion is > 0)
            {
                _minimumVersions.AddOrUpdate(
                    userId,
                    minimumSecurityVersion.Value,
                    (_, current) => Math.Max(current, minimumSecurityVersion.Value));
            }

            _cache.Remove(userId);
        }

        AuthSecurityMetrics.RecordAuthFenceL1("eviction");
    }

    public void Dispose() => _cache.Dispose();
}
