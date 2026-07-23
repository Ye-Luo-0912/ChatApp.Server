using ChatApp.Realtime.Abstractions.Events;
using ChatApp.Realtime.Integration;
using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// 消费 Realtime AccountCleanupCompleted / AttachmentBlobsPurge：
/// - Completed：推进 AccountCleanupSaga；
/// - AttachmentBlobsPurge：入队附件 blob 删除墓碑；
/// 乱序有限 NAK；非法 / 耗尽 → Server DLQ + ACK；周期性将超时 Pending 标为 Failed。
/// 若 RealtimeIntegration:Url 未配置，则无总线消费（本 Worker 仅跑 stale-fail；附件 GC 依赖账号删除本地入队）。
/// </summary>
public sealed class AccountCleanupSagaWorker(
    IServiceScopeFactory scopeFactory,
    IRealtimeMessageBus? bus,
    IOptions<AccountCleanupSagaOptions> options,
    ILogger<AccountCleanupSagaWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var staleTask = RunStaleFailLoopAsync(stoppingToken);
        var consumeTask = bus is null
            ? Task.CompletedTask
            : RunConsumeLoopAsync(bus, stoppingToken);

        if (bus is null)
        {
            logger.LogInformation(
                "RealtimeIntegration:Url 未配置，跳过 AccountCleanup / AttachmentBlobsPurge 消费；" +
                "附件 blob 删除依赖账号注销本地墓碑入队");
        }

        await Task.WhenAll(staleTask, consumeTask);
    }

    private async Task RunConsumeLoopAsync(IRealtimeMessageBus messageBus, CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "AccountCleanupSagaWorker 开始消费 AccountCleanupCompleted / AttachmentBlobsPurge");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var delivery in messageBus.ConsumeAccountCleanupEventsAsync(stoppingToken))
                {
                    try
                    {
                        await HandleDeliveryAsync(delivery, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "处理账号清理事件失败，将 NAK。Type={Type}；EventId={EventId}",
                            delivery.Event.Type,
                            delivery.Event.EventId);
                        await delivery.NakAsync(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AccountCleanupSaga 消费循环异常，将重试");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task HandleDeliveryAsync(RealtimeEventDelivery delivery, CancellationToken ct)
    {
        var evt = delivery.Event;
        if (evt.Type == RealtimeEventType.AttachmentBlobsPurge)
        {
            await HandleAttachmentBlobsPurgeAsync(delivery, ct);
            return;
        }

        if (evt.Type != RealtimeEventType.AccountCleanupCompleted)
        {
            // UserAccountDeleted 由 Realtime AccountCleanupWorker 处理；Server 直接 ACK。
            await delivery.AckAsync(ct);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAccountCleanupSagaService>();
        var concrete = scope.ServiceProvider.GetRequiredService<AccountCleanupSagaService>();
        var result = await concrete.TryApplyCompletedEventAsync(evt, ct);
        var opts = options.Value;

        switch (result)
        {
            case AccountCleanupApplyResult.Completed:
            case AccountCleanupApplyResult.AlreadyCompleted:
            case AccountCleanupApplyResult.DuplicateDelivery:
                await delivery.AckAsync(ct);
                return;

            case AccountCleanupApplyResult.MissingSaga:
            {
                var max = Math.Max(1, opts.MaxMissingSagaDeliveries);
                var delivered = delivery.DeliveryCount ?? 1;
                if (delivered >= (ulong)max)
                {
                    await svc.RecordDeadLetterAsync(
                        evt.EventId,
                        evt.TargetUserId,
                        evt.PayloadJson,
                        AccountCleanupDeadLetterReason.MissingSagaExhausted,
                        $"missing_saga after {delivered} deliveries",
                        delivery.DeliveryCount,
                        ct);
                    await delivery.AckAsync(ct);
                    return;
                }

                var delay = TimeSpan.FromSeconds(Math.Max(1, opts.MissingSagaNakDelaySeconds));
                logger.LogWarning(
                    "AccountCleanupCompleted 乱序（无 Saga），NAK 重试。UserId={UserId}；EventId={EventId}；Delivery={Delivery}/{Max}",
                    evt.TargetUserId,
                    evt.EventId,
                    delivered,
                    max);
                await delivery.NakAsync(delay, ct);
                return;
            }

            case AccountCleanupApplyResult.EventIdMismatch:
                await svc.RecordDeadLetterAsync(
                    evt.EventId,
                    evt.TargetUserId,
                    evt.PayloadJson,
                    AccountCleanupDeadLetterReason.EventIdMismatch,
                    "completed EventId does not match saga EventId",
                    delivery.DeliveryCount,
                    ct);
                await delivery.AckAsync(ct);
                return;

            case AccountCleanupApplyResult.InvalidCompletedEventId:
                await svc.RecordDeadLetterAsync(
                    evt.EventId,
                    evt.TargetUserId,
                    evt.PayloadJson,
                    AccountCleanupDeadLetterReason.InvalidCompletedEventId,
                    "completed EventId missing cleanup-done: prefix",
                    delivery.DeliveryCount,
                    ct);
                await delivery.AckAsync(ct);
                return;

            default:
                await delivery.NakAsync(TimeSpan.FromSeconds(5), ct);
                return;
        }
    }

    private async Task HandleAttachmentBlobsPurgeAsync(RealtimeEventDelivery delivery, CancellationToken ct)
    {
        var evt = delivery.Event;
        if (string.IsNullOrWhiteSpace(evt.PayloadJson))
        {
            logger.LogWarning(
                "AttachmentBlobsPurge 缺少 PayloadJson，ACK 跳过。EventId={EventId}",
                evt.EventId);
            await delivery.AckAsync(ct);
            return;
        }

        AttachmentBlobsPurgePayload? payload;
        try
        {
            payload = System.Text.Json.JsonSerializer.Deserialize<AttachmentBlobsPurgePayload>(
                evt.PayloadJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "AttachmentBlobsPurge PayloadJson 反序列化失败，ACK 跳过。EventId={EventId}",
                evt.EventId);
            await delivery.AckAsync(ct);
            return;
        }

        if (payload?.ObjectKeys is null || payload.ObjectKeys.Count == 0)
        {
            await delivery.AckAsync(ct);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var deletes = scope.ServiceProvider.GetRequiredService<IAttachmentBlobDeleteService>();
        await deletes.EnqueueAsync(
                payload.ObjectKeys,
                userId: payload.UserId != 0 ? payload.UserId : evt.TargetUserId,
                attachmentId: null,
                ct)
            .ConfigureAwait(false);

        logger.LogInformation(
            "AttachmentBlobsPurge 已入队墓碑 UserId={UserId} Keys={Count} Chunk={Index}/{Total} EventId={EventId}",
            payload.UserId,
            payload.ObjectKeys.Count,
            payload.ChunkIndex,
            payload.ChunkCount,
            evt.EventId);

        await delivery.AckAsync(ct);
    }

    private async Task RunStaleFailLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = options.Value;
            var intervalMinutes = Math.Max(1, opts.StalePollIntervalMinutes);
            try
            {
                if (opts.PendingTimeoutHours > 0)
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var svc = scope.ServiceProvider.GetRequiredService<IAccountCleanupSagaService>();
                    await svc.FailStalePendingAsync(
                        TimeSpan.FromHours(opts.PendingTimeoutHours),
                        stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AccountCleanupSaga 超时扫描失败");
            }

            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }
    }
}
