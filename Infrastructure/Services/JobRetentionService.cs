using Core.Interfaces;
using Core.Models.Export;
using Core.Models.Security;
using Core.Settings;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Bounded, idempotent retention for durable worker history. Deletes are
/// selected by indexed IDs in small batches, so retention never materializes a
/// cold table or competes with the worker claim hot path.
/// </summary>
public sealed class JobRetentionService(
    UserDbContext db,
    IOptions<JobRetentionPolicy> options,
    ILogger<JobRetentionService> logger) : IJobRetentionService
{
    public async Task<JobRetentionResult> PurgeAsync(CancellationToken cancellationToken = default)
    {
        var policy = options.Value;
        var batch = Math.Clamp(policy.BatchSize, 1, 5_000);
        var now = DateTimeOffset.UtcNow;

        var scanJobs = await DeleteIdsAsync(
                db.AttachmentScanJobs
                    .Where(x => (x.Status == AttachmentScanJobStatus.Done
                                 || x.Status == AttachmentScanJobStatus.DeadLetter)
                                && x.CompletedAt != null
                                && x.CompletedAt < now.AddDays(-Math.Max(1, policy.ScanJobRetentionDays)))
                    .OrderBy(x => x.CompletedAt)
                    .Select(x => x.Id),
                db.AttachmentScanJobs,
                batch,
                cancellationToken)
            .ConfigureAwait(false);

        var projections = await DeleteIdsAsync(
                db.AttachmentScanProjections
                    .Where(x => (x.Status == AttachmentScanProjectionStatus.Done
                                 || x.Status == AttachmentScanProjectionStatus.DeadLetter)
                                && x.CompletedAt != null
                                && x.CompletedAt < now.AddDays(-Math.Max(1, policy.ScanProjectionRetentionDays)))
                    .OrderBy(x => x.CompletedAt)
                    .Select(x => x.Id),
                db.AttachmentScanProjections,
                batch,
                cancellationToken)
            .ConfigureAwait(false);

        var audits = await DeleteIdsAsync(
                db.AttachmentScanAudits
                    .Where(x => x.CreatedAt < now.AddDays(-Math.Max(1, policy.ScanAuditRetentionDays)))
                    .OrderBy(x => x.CreatedAt)
                    .Select(x => x.Id),
                db.AttachmentScanAudits,
                batch,
            cancellationToken)
            .ConfigureAwait(false);

        var confirmSagas = await DeleteIdsAsync(
                db.AttachmentConfirmSagas
                    .Where(x => (x.Status == AttachmentConfirmSagaStatus.Completed
                                 || x.Status == AttachmentConfirmSagaStatus.Failed)
                                && (x.CompletedAt ?? x.UpdatedAt)
                                    < now.AddDays(-Math.Max(1, policy.AttachmentConfirmSagaRetentionDays)))
                    .OrderBy(x => x.CompletedAt ?? x.UpdatedAt)
                    .Select(x => x.Id),
                db.AttachmentConfirmSagas,
                batch,
                cancellationToken)
            .ConfigureAwait(false);

        var avatarFinalizationSagas = await DeleteIdsAsync(
                db.AvatarFinalizationSagas
                    .Where(x => (x.Status == AvatarFinalizationSagaStatus.Completed
                                 || x.Status == AvatarFinalizationSagaStatus.Abandoned
                                 || x.Status == AvatarFinalizationSagaStatus.Failed)
                                && (x.CompletedAt ?? x.UpdatedAt)
                                    < now.AddDays(-Math.Max(1, policy.AvatarFinalizationSagaRetentionDays)))
                    .OrderBy(x => x.CompletedAt ?? x.UpdatedAt)
                    .Select(x => x.Id),
                db.AvatarFinalizationSagas,
                batch,
                cancellationToken)
            .ConfigureAwait(false);

        var blobDeletes = await DeleteIdsAsync(
                db.AttachmentBlobDeleteJobs
                    .Where(x => (x.Status == AttachmentBlobDeleteJobStatus.Done
                                 || x.Status == AttachmentBlobDeleteJobStatus.DeadLetter)
                                && x.CompletedAt != null
                                && x.CompletedAt < now.AddDays(-Math.Max(1, policy.AttachmentBlobDeleteRetentionDays)))
                    .OrderBy(x => x.CompletedAt)
                    .Select(x => x.Id),
                db.AttachmentBlobDeleteJobs,
                batch,
                cancellationToken)
            .ConfigureAwait(false);

        var loginAudits = await DeleteIdsAsync(
                db.LoginAuditOutbox
                    .Where(x => (x.Status == LoginAuditOutboxStatus.Completed
                                 || x.Status == LoginAuditOutboxStatus.DeadLetter)
                                && (x.CompletedAt ?? x.UpdatedAt)
                                    < now.AddDays(-Math.Max(1, policy.LoginAuditRetentionDays)))
                    .OrderBy(x => x.CompletedAt ?? x.UpdatedAt)
                    .Select(x => x.Id),
                db.LoginAuditOutbox,
                batch,
                cancellationToken)
            .ConfigureAwait(false);

        var loginRisk = await DeleteIdsAsync(
                db.LoginRiskOutbox
                    .Where(x => (x.Status == LoginRiskOutboxStatus.Completed
                                 || x.Status == LoginRiskOutboxStatus.DeadLetter)
                                && (x.CompletedAt ?? x.UpdatedAt)
                                    < now.AddDays(-Math.Max(1, policy.LoginRiskRetentionDays)))
                    .OrderBy(x => x.CompletedAt ?? x.UpdatedAt)
                    .Select(x => x.Id),
                db.LoginRiskOutbox,
                batch,
                cancellationToken)
            .ConfigureAwait(false);

        var result = new JobRetentionResult(
            scanJobs,
            projections,
            audits,
            blobDeletes,
            loginAudits,
            loginRisk,
            confirmSagas,
            avatarFinalizationSagas);
        JobRetentionMetrics.RecordDeleted("attachment_scan", scanJobs);
        JobRetentionMetrics.RecordDeleted("attachment_projection", projections);
        JobRetentionMetrics.RecordDeleted("attachment_scan_audit", audits);
        JobRetentionMetrics.RecordDeleted("attachment_confirm_saga", confirmSagas);
        JobRetentionMetrics.RecordDeleted("avatar_finalization_saga", avatarFinalizationSagas);
        JobRetentionMetrics.RecordDeleted("attachment_blob_delete", blobDeletes);
        JobRetentionMetrics.RecordDeleted("login_audit", loginAudits);
        JobRetentionMetrics.RecordDeleted("login_risk", loginRisk);
        if (result.Total > 0)
        {
            logger.LogInformation(
                "Worker 作业 retention 完成 ScanJobs={ScanJobs} Projections={Projections} Audits={Audits} ConfirmSagas={ConfirmSagas} AvatarFinalizationSagas={AvatarFinalizationSagas} BlobDeletes={BlobDeletes} LoginAudits={LoginAudits} LoginRisk={LoginRisk}",
                result.ScanJobs,
                result.ScanProjections,
                result.ScanAudits,
                result.AttachmentConfirmSagas,
                result.AvatarFinalizationSagas,
                result.AttachmentBlobDeleteJobs,
                result.LoginAuditOutbox,
                result.LoginRiskOutbox);
        }

        return result;
    }

    private static async Task<int> DeleteIdsAsync<TEntity>(
        IQueryable<long> idsQuery,
        DbSet<TEntity> set,
        int batch,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var ids = await idsQuery.Take(batch).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0)
            return 0;

        return await set.Where(entity => ids.Contains(EF.Property<long>(entity, "Id")))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
