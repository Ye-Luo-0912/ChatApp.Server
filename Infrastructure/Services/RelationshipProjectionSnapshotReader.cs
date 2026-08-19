using System.Data;
using ChatApp.Realtime.Abstractions.Events;
using ChatApp.Realtime.Abstractions.Relationships;
using Core.Models.Friend;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>
/// Reads one complete owner/list snapshot under PostgreSQL repeatable-read isolation.
/// Mutations update relationship rows and their version clock in one transaction, so the
/// returned items and version always describe the same database snapshot.
/// </summary>
public sealed class RelationshipProjectionSnapshotReader(UserDbContext db)
{
    private const int MaxPageSize = 500;
    private const int MaxSnapshotItems = 100_000;

    public async Task<RelationshipProjectionStreamPage> ListStreamsAsync(
        long? afterOwnerUserId,
        RelationshipProjectionListType? afterListType,
        int limit,
        CancellationToken ct = default)
    {
        if (afterOwnerUserId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(afterOwnerUserId));
        if (afterListType is not null && !Enum.IsDefined(afterListType.Value))
            throw new ArgumentOutOfRangeException(nameof(afterListType));
        if (afterOwnerUserId is null != (afterListType is null))
            throw new ArgumentException("Both stream cursor components must be supplied together.");
        if (limit is < 1 or > MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var versionStreams = db.RelationshipProjectionVersions.AsNoTracking()
            .Select(row => new { row.OwnerUserId, row.ListType });
        var friendStreams = db.Friendships.AsNoTracking()
            .Where(row => !row.IsDeleted)
            .Select(row => new
            {
                OwnerUserId = row.UserId,
                ListType = (byte)RelationshipProjectionListType.Friends
            });
        var requesters = db.FriendRequests.AsNoTracking()
            .Where(row => row.Status == RequestStatus.Pending)
            .Select(row => new
            {
                OwnerUserId = row.RequesterId,
                ListType = (byte)RelationshipProjectionListType.FriendRequests
            });
        var targets = db.FriendRequests.AsNoTracking()
            .Where(row => row.Status == RequestStatus.Pending)
            .Select(row => new
            {
                OwnerUserId = row.TargetUserId,
                ListType = (byte)RelationshipProjectionListType.FriendRequests
            });
        var blockers = db.BlockRecords.AsNoTracking()
            .Select(row => new
            {
                OwnerUserId = row.BlockerId,
                ListType = (byte)RelationshipProjectionListType.BlockedUsers
            });
        // 无任何关系数据的用户同样需要三份流，否则 rebuild 不会为其建立空快照基线，
        // TCP 读取将永远返回 relationship_read_projection_unavailable 而无法与 HTTP 空列表一致。
        var users = db.Users.AsNoTracking().Select(row => row.Id);
        var userFriendStreams = users.Select(id => new
        {
            OwnerUserId = id,
            ListType = (byte)RelationshipProjectionListType.Friends
        });
        var userRequestStreams = users.Select(id => new
        {
            OwnerUserId = id,
            ListType = (byte)RelationshipProjectionListType.FriendRequests
        });
        var userBlockedStreams = users.Select(id => new
        {
            OwnerUserId = id,
            ListType = (byte)RelationshipProjectionListType.BlockedUsers
        });

        var streams = versionStreams
            .Union(friendStreams)
            .Union(requesters)
            .Union(targets)
            .Union(blockers)
            .Union(userFriendStreams)
            .Union(userRequestStreams)
            .Union(userBlockedStreams);
        if (afterOwnerUserId is { } owner && afterListType is { } listType)
        {
            var numericListType = (byte)listType;
            streams = streams.Where(row =>
                row.OwnerUserId > owner
                || row.OwnerUserId == owner && row.ListType > numericListType);
        }

        var rows = await (
                from stream in streams
                join version in db.RelationshipProjectionVersions.AsNoTracking()
                    on new { stream.OwnerUserId, stream.ListType }
                    equals new { version.OwnerUserId, version.ListType }
                    into versions
                from version in versions.DefaultIfEmpty()
                orderby stream.OwnerUserId, stream.ListType
                select new
                {
                    stream.OwnerUserId,
                    stream.ListType,
                    Version = version == null ? 0 : version.Version
                })
            .Take(limit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = rows.Count > limit;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);
        var items = rows.Select(static row => new RelationshipProjectionStreamDescriptor(
                row.OwnerUserId,
                (RelationshipProjectionListType)row.ListType,
                row.Version))
            .ToArray();
        var last = items.LastOrDefault();
        return new RelationshipProjectionStreamPage(
            items,
            hasMore,
            hasMore ? last?.OwnerUserId : null,
            hasMore ? last?.ListType : null);
    }

    public async Task<RelationshipProjectionStreamSnapshot> ReadStreamAsync(
        long ownerUserId,
        RelationshipProjectionListType listType,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerUserId);
        if (!Enum.IsDefined(listType))
            throw new ArgumentOutOfRangeException(nameof(listType));

        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
            .ConfigureAwait(false);
        var numericListType = (byte)listType;
        var version = await db.RelationshipProjectionVersions.AsNoTracking()
            .Where(row => row.OwnerUserId == ownerUserId && row.ListType == numericListType)
            .Select(row => (long?)row.Version)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? 0;

        var items = listType switch
        {
            RelationshipProjectionListType.Friends =>
                await ReadFriendsAsync(ownerUserId, ct).ConfigureAwait(false),
            RelationshipProjectionListType.FriendRequests =>
                await ReadRequestsAsync(ownerUserId, ct).ConfigureAwait(false),
            RelationshipProjectionListType.BlockedUsers =>
                await ReadBlockedUsersAsync(ownerUserId, ct).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(listType))
        };
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        if (items.Count > MaxSnapshotItems)
            throw new InvalidOperationException("Relationship projection stream exceeds the snapshot limit.");

        var ordered = items.OrderBy(static item => item.ResourceId, StringComparer.Ordinal).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(ordered[index - 1].ResourceId, ordered[index].ResourceId, StringComparison.Ordinal))
                throw new InvalidOperationException("Relationship projection contains duplicate resource ids.");
        }

        var resourceHash = RelationshipProjectionSnapshotHash.Compute(
            ordered.Select(static item => item.ResourceId));
        var capturedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new RelationshipProjectionStreamSnapshot
        {
            SnapshotId = RelationshipEventIdFactory.CreateRelationshipProjectionSnapshotId(
                ownerUserId, listType, version, resourceHash),
            OwnerUserId = ownerUserId,
            ListType = listType,
            Version = version,
            CapturedAtMs = capturedAtMs,
            ItemCount = ordered.Length,
            ResourceHash = resourceHash,
            Items = ordered
        };
    }

    public async Task<RelationshipProjectionStreamDigest> ReadStreamDigestAsync(
        long ownerUserId,
        RelationshipProjectionListType listType,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerUserId);
        if (!Enum.IsDefined(listType))
            throw new ArgumentOutOfRangeException(nameof(listType));

        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
            .ConfigureAwait(false);
        var numericListType = (byte)listType;
        var version = await db.RelationshipProjectionVersions.AsNoTracking()
            .Where(row => row.OwnerUserId == ownerUserId && row.ListType == numericListType)
            .Select(row => (long?)row.Version)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? 0;
        var resourceIds = await ReadResourceIdsAsync(ownerUserId, listType, ct)
            .ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        if (resourceIds.Count > MaxSnapshotItems)
            throw new InvalidOperationException("Relationship projection stream exceeds the snapshot limit.");

        var ordered = resourceIds.Order(StringComparer.Ordinal).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(ordered[index - 1], ordered[index], StringComparison.Ordinal))
                throw new InvalidOperationException("Relationship projection contains duplicate resource ids.");
        }

        return new RelationshipProjectionStreamDigest(
            ownerUserId,
            listType,
            version,
            ordered.Length,
            RelationshipProjectionSnapshotHash.Compute(ordered),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private Task<List<string>> ReadResourceIdsAsync(
        long ownerUserId,
        RelationshipProjectionListType listType,
        CancellationToken ct) =>
        listType switch
        {
            RelationshipProjectionListType.Friends =>
                ReadFriendResourceIdsAsync(ownerUserId, ct),
            RelationshipProjectionListType.FriendRequests =>
                ReadRequestResourceIdsAsync(ownerUserId, ct),
            RelationshipProjectionListType.BlockedUsers =>
                ReadBlockedUserResourceIdsAsync(ownerUserId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(listType))
        };

    private async Task<List<string>> ReadFriendResourceIdsAsync(
        long ownerUserId,
        CancellationToken ct)
    {
        var ids = await db.Friendships.AsNoTracking()
            .Where(row => row.UserId == ownerUserId && !row.IsDeleted)
            .OrderBy(row => row.FriendId)
            .Take(MaxSnapshotItems + 1)
            .Select(row => row.FriendId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return ids.Select(static id => id.ToString()).ToList();
    }

    private async Task<List<string>> ReadRequestResourceIdsAsync(
        long ownerUserId,
        CancellationToken ct)
    {
        var ids = await db.FriendRequests.AsNoTracking()
            .Where(row => row.Status == RequestStatus.Pending
                          && (row.RequesterId == ownerUserId || row.TargetUserId == ownerUserId))
            .OrderBy(row => row.RequesterId < row.TargetUserId ? row.RequesterId : row.TargetUserId)
            .ThenBy(row => row.RequesterId < row.TargetUserId ? row.TargetUserId : row.RequesterId)
            .Take(MaxSnapshotItems + 1)
            .Select(row => new { row.RequesterId, row.TargetUserId })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return ids.Select(static row => row.RequesterId < row.TargetUserId
                ? $"{row.RequesterId}:{row.TargetUserId}"
                : $"{row.TargetUserId}:{row.RequesterId}")
            .ToList();
    }

    private async Task<List<string>> ReadBlockedUserResourceIdsAsync(
        long ownerUserId,
        CancellationToken ct)
    {
        var ids = await db.BlockRecords.AsNoTracking()
            .Where(row => row.BlockerId == ownerUserId)
            .OrderBy(row => row.BlockedUserId)
            .Take(MaxSnapshotItems + 1)
            .Select(row => row.BlockedUserId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return ids.Select(static id => id.ToString()).ToList();
    }

    private async Task<List<RelationshipProjectionSnapshotItem>> ReadFriendsAsync(
        long ownerUserId,
        CancellationToken ct)
    {
        var rows = await db.Friendships.AsNoTracking()
            .Where(row => row.UserId == ownerUserId && !row.IsDeleted)
            .OrderBy(row => row.FriendId)
            .Take(MaxSnapshotItems + 1)
            .Select(row => new { row.FriendId, row.CreatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(row => new RelationshipProjectionSnapshotItem
        {
            ResourceId = row.FriendId.ToString(),
            SubjectUserId = row.FriendId,
            ActorUserId = ownerUserId,
            State = "Accepted",
            OccurredAtMs = ToUnixTimeMilliseconds(row.CreatedAt)
        }).ToList();
    }

    private async Task<List<RelationshipProjectionSnapshotItem>> ReadRequestsAsync(
        long ownerUserId,
        CancellationToken ct)
    {
        var rows = await db.FriendRequests.AsNoTracking()
            .Where(row => row.Status == RequestStatus.Pending
                          && (row.RequesterId == ownerUserId || row.TargetUserId == ownerUserId))
            .OrderBy(row => row.RequesterId < row.TargetUserId ? row.RequesterId : row.TargetUserId)
            .ThenBy(row => row.RequesterId < row.TargetUserId ? row.TargetUserId : row.RequesterId)
            .Take(MaxSnapshotItems + 1)
            .Select(row => new
            {
                row.RequesterId,
                row.TargetUserId,
                row.Message,
                row.CreatedAt
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(row => new RelationshipProjectionSnapshotItem
        {
            ResourceId = row.RequesterId < row.TargetUserId
                ? $"{row.RequesterId}:{row.TargetUserId}"
                : $"{row.TargetUserId}:{row.RequesterId}",
            SubjectUserId = row.RequesterId == ownerUserId ? row.TargetUserId : row.RequesterId,
            ActorUserId = row.RequesterId,
            State = "Pending",
            Message = row.Message,
            OccurredAtMs = ToUnixTimeMilliseconds(row.CreatedAt)
        }).ToList();
    }

    private async Task<List<RelationshipProjectionSnapshotItem>> ReadBlockedUsersAsync(
        long ownerUserId,
        CancellationToken ct)
    {
        var rows = await db.BlockRecords.AsNoTracking()
            .Where(row => row.BlockerId == ownerUserId)
            .OrderBy(row => row.BlockedUserId)
            .Take(MaxSnapshotItems + 1)
            .Select(row => new { row.BlockedUserId, row.BlockedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(row => new RelationshipProjectionSnapshotItem
        {
            ResourceId = row.BlockedUserId.ToString(),
            SubjectUserId = row.BlockedUserId,
            ActorUserId = ownerUserId,
            State = "Blocked",
            OccurredAtMs = ToUnixTimeMilliseconds(row.BlockedAt)
        }).ToList();
    }

    private static long ToUnixTimeMilliseconds(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }
}
