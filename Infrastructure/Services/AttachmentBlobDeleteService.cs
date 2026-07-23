using Core.Interfaces;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public sealed class AttachmentBlobDeleteService(
    UserDbContext db,
    IAttachmentStorage storage,
    IOptions<AttachmentStorageOptions> options,
    ILogger<AttachmentBlobDeleteService> logger) : IAttachmentBlobDeleteService
{
    public Task EnqueueAsync(
        IEnumerable<string> objectKeys,
        long? userId = null,
        string? attachmentId = null,
        CancellationToken cancellationToken = default)
        => EnqueueAsync(
            objectKeys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => (k.Trim(), attachmentId)),
            userId,
            cancellationToken);

    public async Task EnqueueAsync(
        IEnumerable<(string ObjectKey, string? AttachmentId)> items,
        long? userId = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = new List<AttachmentBlobDeleteJob>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (objectKey, attachmentId) in items)
        {
            if (string.IsNullOrWhiteSpace(objectKey))
                continue;
            var key = objectKey.Trim();
            if (!seen.Add(key))
                continue;

            var exists = await db.AttachmentBlobDeleteJobs
                .AnyAsync(
                    j => j.ObjectKey == key && j.Status == AttachmentBlobDeleteJobStatus.Pending,
                    cancellationToken)
                .ConfigureAwait(false);
            if (exists)
                continue;

            pending.Add(new AttachmentBlobDeleteJob
            {
                ObjectKey = key,
                AttachmentId = attachmentId,
                UserId = userId,
                Status = AttachmentBlobDeleteJobStatus.Pending,
                AttemptCount = 0,
                NextAttemptAt = now,
                CreatedAt = now,
            });
        }

        if (pending.Count == 0)
            return;

        db.AttachmentBlobDeleteJobs.AddRange(pending);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        AuthSecurityMetrics.AttachmentPendingDeleteDelta(pending.Count);
        logger.LogInformation(
            "已入队 {Count} 条附件 blob 删除墓碑 UserId={UserId}",
            pending.Count,
            userId);
    }

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var batchSize = Math.Clamp(opts.DeleteBatchSize, 1, 500);
        var maxAttempts = Math.Max(1, opts.MaxDeleteAttempts);
        var now = DateTimeOffset.UtcNow;

        var due = await db.AttachmentBlobDeleteJobs
            .Where(j => j.Status == AttachmentBlobDeleteJobStatus.Pending && j.NextAttemptAt <= now)
            .OrderBy(j => j.NextAttemptAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var deleted = 0;
        foreach (var job in due)
        {
            try
            {
                await storage.DeleteAsync(job.ObjectKey, cancellationToken).ConfigureAwait(false);
                job.Status = AttachmentBlobDeleteJobStatus.Done;
                job.CompletedAt = DateTimeOffset.UtcNow;
                job.LastError = null;
                AuthSecurityMetrics.AttachmentBlobDelete("success");
                AuthSecurityMetrics.AttachmentPendingDeleteDelta(-1);
                deleted++;
            }
            catch (Exception ex)
            {
                job.AttemptCount = Math.Max(1, job.AttemptCount + 1);
                job.LastError = Truncate(ex.Message, 500);
                job.NextAttemptAt = DateTimeOffset.UtcNow.Add(ComputeBackoff(opts, job.AttemptCount));
                AuthSecurityMetrics.AttachmentBlobDelete("failed");
                logger.LogWarning(
                    ex,
                    "附件 blob 删除失败，保留 Pending 墓碑 JobId={Id} Key={Key} Attempt={Attempt}",
                    job.Id,
                    job.ObjectKey,
                    job.AttemptCount);

                if (job.AttemptCount >= maxAttempts)
                {
                    AuthSecurityMetrics.AttachmentBlobDelete("exhausted");
                    logger.LogError(
                        "附件 blob 删除重试已耗尽 JobId={Id} Key={Key} Attempts={Attempt}",
                        job.Id,
                        job.ObjectKey,
                        job.AttemptCount);
                }
            }
        }

        if (due.Count > 0)
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return deleted;
    }

    private static TimeSpan ComputeBackoff(AttachmentStorageOptions opts, int attemptCount)
    {
        var baseSeconds = Math.Max(5, opts.DeleteBackoffSeconds);
        var exp = Math.Min(attemptCount - 1, 10);
        var seconds = Math.Min(3600, baseSeconds * Math.Pow(2, Math.Max(0, exp)));
        return TimeSpan.FromSeconds(seconds);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}

/// <summary>DI 作用域工厂封装，供 BackgroundService 入队。</summary>
public sealed class AttachmentBlobDeleteEnqueuer(IServiceScopeFactory scopeFactory) 
{
    public async Task EnqueueAsync(
        IEnumerable<string> objectKeys,
        long? userId = null,
        string? attachmentId = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAttachmentBlobDeleteService>();
        await svc.EnqueueAsync(objectKeys, userId, attachmentId, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnqueueAsync(
        IEnumerable<(string ObjectKey, string? AttachmentId)> items,
        long? userId = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAttachmentBlobDeleteService>();
        await svc.EnqueueAsync(items, userId, cancellationToken).ConfigureAwait(false);
    }
}
