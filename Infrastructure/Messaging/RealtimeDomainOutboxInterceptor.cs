using ChatApp.Realtime.Abstractions.Events;
using ChatApp.Realtime.Integration.Outbox;
using ChatApp.Realtime.Integration.Serialization;
using Core.Models.Friend;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.Messaging;

public sealed class RealtimeDomainOutboxInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        AppendNotifications(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppendNotifications(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void AppendNotifications(DbContext? context)
    {
        if (context is null || context.ChangeTracker.Entries<RealtimeIntegrationOutboxItem>()
                .Any(entry => entry.State == EntityState.Added))
            return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var events = new List<RealtimeEvent>();

        foreach (var entry in context.ChangeTracker.Entries<FriendRequest>()
                     .Where(IsChanged))
        {
            var request = entry.Entity;
            AddEvent(events, RealtimeEventType.FriendRequestListChanged, request.TargetUserId,
                request.RequesterId, "friend-request", request.Status.ToString(), request.RequestId.ToString(),
                request.Message, now);
            AddEvent(events, RealtimeEventType.FriendRequestListChanged, request.RequesterId,
                request.TargetUserId, "friend-request", request.Status.ToString(), request.RequestId.ToString(),
                request.Message, now);
        }

        foreach (var entry in context.ChangeTracker.Entries<UserFriendEntry>()
                     .Where(IsChanged))
        {
            var friendship = entry.Entity;
            var action = entry.State == EntityState.Deleted || friendship.IsDeleted ? "deleted" : "changed";
            AddEvent(events, RealtimeEventType.FriendListChanged, friendship.UserId,
                friendship.FriendId, "friendship", action, friendship.FriendshipId.ToString(), null, now);
        }

        foreach (var entry in context.ChangeTracker.Entries<BlockRecord>()
                     .Where(IsChanged))
        {
            var block = entry.Entity;
            AddEvent(events, RealtimeEventType.BlockedListChanged, block.BlockerId,
                block.BlockedUserId, "blocked-user",
                entry.State == EntityState.Deleted ? "unblocked" : "blocked",
                block.BlockId.ToString(), null, now);
        }

        if (events.Count == 0)
            return;

        context.Set<RealtimeIntegrationOutboxItem>()
            .AddRange(events.Select(RealtimeIntegrationOutboxItem.FromEvent));
    }

    private static bool IsChanged<TEntity>(EntityEntry<TEntity> entry) where TEntity : class =>
        entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted;

    private static void AddEvent(
        ICollection<RealtimeEvent> events,
        RealtimeEventType type,
        long targetUserId,
        long actorUserId,
        string resource,
        string action,
        string resourceId,
        string? message,
        long occurredAtMs)
    {
        if (targetUserId <= 0)
            return;

        events.Add(new RealtimeEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            Type = type,
            TargetUserId = targetUserId,
            ActorUserId = actorUserId > 0 ? actorUserId : null,
            PayloadJson = RealtimeWireSerializer.Serialize(new RealtimeDomainNotificationPayload
            {
                Resource = resource,
                Action = action,
                ResourceId = resourceId,
                Message = message
            }),
            OccurredAtMs = occurredAtMs
        });
    }
}
