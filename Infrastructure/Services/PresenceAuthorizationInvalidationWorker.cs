using System.Text.Json;
using ChatApp.Realtime.Abstractions.Conversations;
using ChatApp.Realtime.Abstractions.Events;
using ChatApp.Realtime.Integration;
using Core.Interfaces.Cache;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// 将 Realtime 群成员变更转换为按用户维护的 Presence membership epoch。
///
/// 事件只负责低延迟推进派生版本；事件丢失时，Presence 投影的短 TTL
/// 仍会触发权威查询。epoch 让正常命中不必重新查询两个 PostgreSQL。
/// </summary>
public sealed class PresenceAuthorizationInvalidationWorker(
    IRealtimeMessageBus? bus,
    IAtomicCacheStore atomicCache,
    ILogger<PresenceAuthorizationInvalidationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (bus is null)
        {
            logger.LogInformation(
                "RealtimeIntegration:Url 未配置，Presence membership epoch 使用短 TTL 回退");
            return;
        }

        logger.LogInformation("PresenceAuthorizationInvalidationWorker 开始消费成员变更事件");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var delivery in bus.ConsumeEventsAsync(stoppingToken))
                {
                    await HandleDeliveryAsync(delivery, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Presence membership epoch 消费循环异常，将重连");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleDeliveryAsync(
        RealtimeEventDelivery delivery,
        CancellationToken cancellationToken)
    {
        var evt = delivery.Event;
        if (!IsMembershipMutation(evt.Type))
        {
            await delivery.AckAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var userIds = ExtractUserIds(evt);
            foreach (var userId in userIds)
            {
                await PresenceAuthorizationCache.AdvanceMembershipEpochAsync(
                        atomicCache,
                        userId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await delivery.AckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Presence membership epoch 推进失败，将 NAK 重试 EventId={EventId}",
                evt.EventId);
            await delivery.NakAsync(TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool IsMembershipMutation(RealtimeEventType type) =>
        type is RealtimeEventType.MemberJoined
            or RealtimeEventType.MemberLeft
            or RealtimeEventType.MemberRemoved
            or RealtimeEventType.MembersAdded
            or RealtimeEventType.ConversationDissolved;

    private static IReadOnlySet<long> ExtractUserIds(RealtimeEvent evt)
    {
        var ids = new HashSet<long>();
        AddPositive(ids, evt.TargetUserId);
        AddPositive(ids, evt.ActorUserId);
        if (evt.TargetUserIds is not null)
        {
            foreach (var userId in evt.TargetUserIds)
                AddPositive(ids, userId);
        }

        // Aggregated remove/leave events normally include the pre-mutation
        // audience in TargetUserIds. Decode the payload as a repair for older
        // producers that only sent the remaining audience.
        if (!string.IsNullOrWhiteSpace(evt.PayloadJson))
        {
            try
            {
                switch (evt.Type)
                {
                    case RealtimeEventType.MemberJoined:
                        AddPositive(
                            ids,
                            JsonSerializer.Deserialize<RealtimeMemberJoinedPayload>(evt.PayloadJson)?.UserId);
                        break;
                    case RealtimeEventType.MemberLeft:
                        AddPositive(
                            ids,
                            JsonSerializer.Deserialize<RealtimeMemberLeftPayload>(evt.PayloadJson)?.UserId);
                        break;
                    case RealtimeEventType.MemberRemoved:
                        AddPositive(
                            ids,
                            JsonSerializer.Deserialize<RealtimeMemberRemovedPayload>(evt.PayloadJson)?.UserId);
                        break;
                    case RealtimeEventType.MembersAdded:
                    {
                        var payload = JsonSerializer.Deserialize<RealtimeMembersAddedPayload>(evt.PayloadJson);
                        if (payload?.Members is not null)
                        {
                            foreach (var member in payload.Members)
                                AddPositive(ids, member.UserId);
                        }

                        break;
                    }
                }
            }
            catch (JsonException)
            {
                // The envelope audience and actor still provide a safe
                // best-effort invalidation for old/malformed payloads.
            }
        }

        return ids;
    }

    private static void AddPositive(ISet<long> ids, long? userId)
    {
        if (userId is > 0)
            ids.Add(userId.Value);
    }

    private static void AddPositive(ISet<long> ids, long userId)
    {
        if (userId > 0)
            ids.Add(userId);
    }
}
