using Core.Models.Export;
using Core.Models.Email;
using Core.Models.Notifications;
using Core.Models.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ChatApp.Realtime.Abstractions.Stores;

namespace Infrastructure.Diagnostics;

/// <summary>
/// Worker-role-only low-frequency queue sampler. It keeps API performance
/// counters free from background polling and exposes one backlog/oldest-age
/// pair for every durable queue.
/// </summary>
public sealed class WorkerBacklogMetricsWorker(
    IServiceScopeFactory scopeFactory,
    WorkerConcurrencyManager concurrencyManager,
    ILogger<WorkerBacklogMetricsWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SampleAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Worker backlog 指标采样失败");
                try
                {
                    await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTimeOffset.UtcNow;

        await SampleAsync(
                "email_dispatch",
                db.EmailOutbox.AsNoTracking()
                    .Where(x => x.Status == EmailOutboxStatus.Pending || x.Status == EmailOutboxStatus.Failed)
                    .Select(x => (DateTimeOffset?)new DateTimeOffset(x.CreatedAt)),
                cancellationToken)
            .ConfigureAwait(false);
        await SampleAsync(
                "notification_dispatch",
                db.NotificationOutbox.AsNoTracking()
                    .Where(x => x.Status == NotificationOutboxStatus.Pending || x.Status == NotificationOutboxStatus.Failed)
                    .Select(x => (DateTimeOffset?)x.CreatedAt),
                cancellationToken)
            .ConfigureAwait(false);
        await SampleAsync(
                "attachment_scan",
                db.AttachmentScanJobs.AsNoTracking()
                    .Where(x => x.Status == AttachmentScanJobStatus.Pending)
                    .Select(x => (DateTimeOffset?)x.CreatedAt),
                cancellationToken)
            .ConfigureAwait(false);
        await SampleAsync(
                "attachment_projection",
                db.AttachmentScanProjections.AsNoTracking()
                    .Where(x => x.Status == AttachmentScanProjectionStatus.Pending)
                    .Select(x => (DateTimeOffset?)x.CreatedAt),
                cancellationToken)
            .ConfigureAwait(false);
        await SampleAsync(
                "attachment_confirm",
                db.AttachmentConfirmSagas.AsNoTracking()
                    .Where(x => x.Status != AttachmentConfirmSagaStatus.Completed
                                && x.Status != AttachmentConfirmSagaStatus.Failed)
                    .Select(x => (DateTimeOffset?)x.CreatedAt),
                cancellationToken)
            .ConfigureAwait(false);
        await SampleAsync(
                "attachment_blob_delete",
                db.AttachmentBlobDeleteJobs.AsNoTracking()
                    .Where(x => x.Status == AttachmentBlobDeleteJobStatus.Pending
                                || x.Status == AttachmentBlobDeleteJobStatus.Processing
                                || x.Status == AttachmentBlobDeleteJobStatus.AwaitingPublication)
                    .Select(x => (DateTimeOffset?)x.CreatedAt),
                cancellationToken)
            .ConfigureAwait(false);
        await SampleAsync(
                "data_export",
                db.DataExportJobs.AsNoTracking()
                    .Where(x => x.Status == DataExportJobStatus.Pending
                                || x.Status == DataExportJobStatus.Processing
                                || x.Status == DataExportJobStatus.CancelRequested)
                    .Select(x => (DateTimeOffset?)x.CreatedAt),
                cancellationToken)
            .ConfigureAwait(false);
        await SampleAsync(
                "moderation_revocation",
                db.ModerationSessionRevocationOutbox.AsNoTracking()
                    .Where(x => x.Status == ModerationSessionRevocationOutboxStatus.Pending
                                || x.Status == ModerationSessionRevocationOutboxStatus.Failed)
                    .Select(x => (DateTimeOffset?)x.CreatedAt),
                cancellationToken)
            .ConfigureAwait(false);
        await SampleAsync(
                "login_audit_outbox",
                db.LoginAuditOutbox.AsNoTracking()
                    .Where(x => x.Status == LoginAuditOutboxStatus.Pending || x.Status == LoginAuditOutboxStatus.Failed)
                    .Select(x => (DateTimeOffset?)x.CreatedAt),
                cancellationToken)
            .ConfigureAwait(false);
        await SampleAsync(
                "account_deletion",
                db.Users.AsNoTracking()
                    .Where(x => x.DeletionScheduledAt != null
                                && x.DeletionScheduledAt <= now
                                && x.DeletionDeadLetterAt == null)
                    .Select(x => x.DeletionScheduledAt),
                cancellationToken)
            .ConfigureAwait(false);
        await SampleAsync(
                "realtime_outbox",
                db.RealtimeOutbox.AsNoTracking()
                    .Where(x => x.Status == (short)RealtimeOutboxStatus.Pending)
                    .Select(x => (DateTimeOffset?)DateTimeOffset.UnixEpoch.AddMilliseconds(x.CreatedAtMs)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SampleAsync(
        string workerName,
        IQueryable<DateTimeOffset?> createdAtQuery,
        CancellationToken cancellationToken)
    {
        var sample = await createdAtQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                Oldest = g.Min(),
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        concurrencyManager.RecordBacklog(workerName, sample?.Count ?? 0);
        concurrencyManager.RecordOldestPendingJob(workerName, sample?.Oldest);
    }
}
