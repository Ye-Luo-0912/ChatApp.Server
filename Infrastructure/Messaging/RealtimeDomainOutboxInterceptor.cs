using ChatApp.Realtime.Abstractions.Events;
using ChatApp.Realtime.Integration.Outbox;
using ChatApp.Realtime.Integration.Serialization;
using Core.Models.Friend;
using Microsoft.EntityFrameworkCore;
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
        if (context is null)
            return;

        List<(object Entity, EntityState State)>? changes = null;
        HashSet<(short EventType, long TargetUserId, string ResourceId)>? existingKeys = null;

        // Entries<T>() 每次都可能触发一次 DetectChanges。只扫描一次完整 ChangeTracker，
        // 再处理少量相关实体，避免一次 SaveChanges 做四次全量检测。
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            switch (entry.Entity)
            {
                case RealtimeIntegrationOutboxItem item when entry.State == EntityState.Added:
                    existingKeys ??= [];
                    existingKeys.Add((item.EventType, item.TargetUserId, ExtractResourceId(item.PayloadJson)));
                    break;
                case FriendRequest or UserFriendEntry or BlockRecord:
                    changes ??= [];
                    changes.Add((entry.Entity, entry.State));
                    break;
            }
        }

        if (changes is null)
            return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var events = new List<RealtimeEvent>(changes.Count * 2);
        existingKeys ??= [];

        foreach (var (entity, state) in changes)
        {
            switch (entity)
            {
                case FriendRequest request:
                    TryAddEvent(events, existingKeys, RealtimeEventType.FriendRequestListChanged, request.TargetUserId,
                        request.RequesterId, "friend-request", request.Status.ToString(), request.RequestId.ToString(),
                        request.Message, now);
                    TryAddEvent(events, existingKeys, RealtimeEventType.FriendRequestListChanged, request.RequesterId,
                        request.TargetUserId, "friend-request", request.Status.ToString(), request.RequestId.ToString(),
                        request.Message, now);
                    break;
                case UserFriendEntry friendship:
                    var action = state == EntityState.Deleted || friendship.IsDeleted ? "deleted" : "changed";
                    TryAddEvent(events, existingKeys, RealtimeEventType.FriendListChanged, friendship.UserId,
                        friendship.FriendId, "friendship", action, friendship.FriendshipId.ToString(), null, now);
                    break;
                case BlockRecord block:
                    TryAddEvent(events, existingKeys, RealtimeEventType.BlockedListChanged, block.BlockerId,
                        block.BlockedUserId, "blocked-user",
                        state == EntityState.Deleted ? "unblocked" : "blocked",
                        block.BlockId.ToString(), null, now);
                    break;
            }
        }

        if (events.Count == 0)
            return;

        context.Set<RealtimeIntegrationOutboxItem>()
            .AddRange(events.Select(RealtimeIntegrationOutboxItem.FromEvent));
    }

    /// <summary>
    /// Extract ResourceId from serialized outbox payload for dedup. Returns empty on failure.
    /// </summary>
    private static string ExtractResourceId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return string.Empty;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("PayloadJson", out var inner) && inner.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                using var innerDoc = System.Text.Json.JsonDocument.Parse(inner.GetString()!);
                if (innerDoc.RootElement.TryGetProperty("ResourceId", out var rid) && rid.ValueKind == System.Text.Json.JsonValueKind.String)
                    return rid.GetString() ?? string.Empty;
            }
            if (doc.RootElement.TryGetProperty("ResourceId", out var direct) && direct.ValueKind == System.Text.Json.JsonValueKind.String)
                return direct.GetString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    private static void TryAddEvent(
        ICollection<RealtimeEvent> events,
        HashSet<(short EventType, long TargetUserId, string ResourceId)> existingKeys,
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

        var key = ((short)type, targetUserId, resourceId);
        if (!existingKeys.Add(key))
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
