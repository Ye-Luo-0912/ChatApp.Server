using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Infrastructure.Diagnostics;

namespace Infrastructure.Services;

/// <summary>
/// Scope-safe durable store for export jobs. The export itself already fences
/// the Ready publication inside <see cref="DataExportService.ProcessJobAsync"/>;
/// this adapter supplies the common claim/renew/retry boundary around it.
/// </summary>
public sealed class DataExportJobStore(
    IServiceScopeFactory scopeFactory,
    IOptions<DataExportStorageOptions> options) : ILeasedJobStore<DataExportJob>
{
    private static readonly string ProcessOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:export";

    public TimeSpan ProcessingLease
        => TimeSpan.FromSeconds(Math.Max(30, options.Value.LeaseSeconds));

    public int MaxAttempts => Math.Max(1, options.Value.MaxJobAttempts);

    public async Task<IReadOnlyList<DataExportJob>> ClaimAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        maxCount = Math.Clamp(maxCount, 1, 256);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var claimed = new List<DataExportJob>(maxCount);

        for (var i = 0; i < maxCount; i++)
        {
            var now = DateTimeOffset.UtcNow;
            var leaseUntil = now.Add(ProcessingLease);
            var owner = CreateOwner();
            var token = Guid.NewGuid().ToString("N");
            (DataExportJob Job, string LeaseToken)? item;

            if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                item = await DataExportJobProcessor.ClaimOneNpgsqlAsync(
                        db, owner, token, now, leaseUntil, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                item = await ClaimInMemoryAsync(
                        db, owner, token, now, leaseUntil, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (item is null)
                break;
            claimed.Add(item.Value.Job);
        }

        return claimed;
    }

    public async Task<LeaseRenewalResult> RenewAsync(
        DataExportJob job,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var updated = await db.DataExportJobs
                .Where(x => x.Id == job.Id
                        && (x.Status == DataExportJobStatus.Processing
                            || x.Status == DataExportJobStatus.CancelRequested)
                        && x.LeaseOwner == job.LeaseOwner
                        && x.LeaseToken == job.LeaseToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.LeaseUntil, DateTimeOffset.UtcNow.Add(ProcessingLease)),
                cancellationToken)
            .ConfigureAwait(false);
        return updated == 1 ? LeaseRenewalResult.Renewed : LeaseRenewalResult.LeaseLost;
    }

    public async Task ExecuteClaimedAsync(
        DataExportJob job,
        CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IDataExportBlobStore>();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionStore>();
        var chatExport = scope.ServiceProvider.GetRequiredService<IRealtimeChatExportReader>();
        var attachmentMeta = scope.ServiceProvider.GetRequiredService<IAttachmentMetadataStore>();
        var stagingBudget = scope.ServiceProvider.GetService<DataExportStagingBudget>();

        try
        {
            await DataExportJobProcessor.ProcessJobAsync(
                    db,
                    blob,
                    sessions,
                    chatExport,
                    attachmentMeta,
                    scopeFactory,
                    job.Id,
                    job.UserId,
                    options.Value,
                    job.LeaseOwner!,
                    job.LeaseToken!,
                    Math.Max(30, options.Value.LeaseSeconds),
                    cancellationToken,
                    stagingBudget)
                .ConfigureAwait(false);
            AuthSecurityMetrics.ExportFinished("ready", ElapsedMilliseconds(started));
        }
        catch (DataExportCancellationRequestedException)
        {
            await DataExportJobProcessor.MarkCancelledAsync(
                    db,
                    job.Id,
                    job.LeaseOwner!,
                    job.LeaseToken!,
                    CancellationToken.None)
                .ConfigureAwait(false);
            AuthSecurityMetrics.ExportFinished("cancelled", ElapsedMilliseconds(started));
        }
        catch
        {
            AuthSecurityMetrics.ExportFinished("failed", ElapsedMilliseconds(started));
            throw;
        }
    }

    public async Task<bool> CompleteAsync(
        DataExportJob job,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var status = await db.DataExportJobs
            .AsNoTracking()
            .Where(x => x.Id == job.Id)
            .Select(x => x.Status)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return status is DataExportJobStatus.Ready or DataExportJobStatus.Cancelled;
    }

    public Task<bool> RetryAsync(
        DataExportJob job,
        string error,
        CancellationToken cancellationToken = default)
        => FinalizeFailureAsync(job, error, deadLetter: false, cancellationToken);

    public Task<bool> DeadLetterAsync(
        DataExportJob job,
        string error,
        CancellationToken cancellationToken = default)
        => FinalizeFailureAsync(job, error, deadLetter: true, cancellationToken);

    private async Task<bool> FinalizeFailureAsync(
        DataExportJob job,
        string error,
        bool deadLetter,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTimeOffset.UtcNow;
        var next = deadLetter
            ? now
            : now.Add(LeasedJobBackoff.ExponentialWithJitter(
                TimeSpan.FromSeconds(5),
                Math.Max(1, job.AttemptCount),
                TimeSpan.FromHours(1)));
        var message = error.Length <= 500 ? error : error[..500];
        var updated = await db.DataExportJobs
            .Where(x => x.Id == job.Id
                        && x.Status == DataExportJobStatus.Processing
                        && x.LeaseOwner == job.LeaseOwner
                        && x.LeaseToken == job.LeaseToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status,
                        deadLetter ? DataExportJobStatus.Failed : DataExportJobStatus.Pending)
                    .SetProperty(x => x.Error, deadLetter
                        ? DataExportJobErrors.ExportFailed
                        : message)
                    .SetProperty(x => x.NextAttemptAt, next)
                    .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseToken, (string?)null),
                cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    private static async Task<(DataExportJob Job, string LeaseToken)?> ClaimInMemoryAsync(
        UserDbContext db,
        string owner,
        string token,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken)
    {
        var candidate = await db.DataExportJobs
            .Where(x => x.Status == DataExportJobStatus.Pending
                        && x.NextAttemptAt <= now
                        || x.Status == DataExportJobStatus.Processing
                        && x.LeaseUntil != null
                        && x.LeaseUntil < now
                        || x.Status == DataExportJobStatus.CancelRequested
                        && (x.LeaseUntil == null || x.LeaseUntil < now))
            .OrderBy(x => x.NextAttemptAt)
            .ThenBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null)
            return null;

        var updated = candidate.Status == DataExportJobStatus.CancelRequested
            ? await db.DataExportJobs
                .Where(x => x.Id == candidate.Id
                            && x.Status == DataExportJobStatus.CancelRequested
                            && (x.LeaseUntil == null || x.LeaseUntil < now))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.LeaseOwner, owner)
                        .SetProperty(x => x.LeaseToken, token)
                        .SetProperty(x => x.LeaseUntil, leaseUntil)
                        .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1),
                    cancellationToken)
                .ConfigureAwait(false)
            : await db.DataExportJobs
                .Where(x => x.Id == candidate.Id
                            && (x.Status == DataExportJobStatus.Pending
                                || (x.Status == DataExportJobStatus.Processing
                                    && x.LeaseUntil != null
                                    && x.LeaseUntil < now)))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.Status, DataExportJobStatus.Processing)
                        .SetProperty(x => x.LeaseOwner, owner)
                        .SetProperty(x => x.LeaseToken, token)
                        .SetProperty(x => x.LeaseUntil, leaseUntil)
                        .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1),
                    cancellationToken)
                .ConfigureAwait(false);
        if (updated != 1)
            return null;

        return (new DataExportJob
        {
            Id = candidate.Id,
            UserId = candidate.UserId,
            Status = candidate.Status == DataExportJobStatus.CancelRequested
                ? DataExportJobStatus.CancelRequested
                : DataExportJobStatus.Processing,
            LeaseOwner = owner,
            LeaseToken = token,
            LeaseUntil = leaseUntil,
            AttemptCount = candidate.AttemptCount + 1,
            CreatedAt = candidate.CreatedAt,
            NextAttemptAt = candidate.NextAttemptAt,
        }, token);
    }

    private static string CreateOwner()
    {
        var value = $"{ProcessOwner}:{Guid.NewGuid():N}";
        return value[..Math.Min(64, value.Length)];
    }

    private static double ElapsedMilliseconds(long started)
        => (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d
           / System.Diagnostics.Stopwatch.Frequency;
}
