using ChatApp.Realtime.Integration;
using ChatApp.Realtime.Integration.Ephemeral;
using Core.Interfaces;
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
        var authorization = scope.ServiceProvider.GetRequiredService<IPresenceAuthorizationService>();
        var allowed = new List<long>(targets.Length);
        try
        {
            var authorized = await authorization
                .AuthorizeAsync(query.WatcherUserId, targets, ct)
                .ConfigureAwait(false);

            allowed.AddRange(authorized);
        }
        catch (Exception ex)
        {
            // 任何关系/跨库查询失败都 fail closed。
            logger.LogWarning(ex, "Presence 授权投影查询失败，拒绝本批次");
            return new PresenceAuthorizeResponse { AllowedUserIds = [] };
        }

        return new PresenceAuthorizeResponse { AllowedUserIds = allowed };
    }
}
