using Core.Interfaces;
using Core.Models.Export;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Services;

/// <summary>为通用租约执行器提供作用域安全的投影存储。</summary>
public sealed class AttachmentScanProjectionJobStore(
    IServiceScopeFactory scopeFactory) : ILeasedJobStore<AttachmentScanProjection>
{
    public async Task<IReadOnlyList<AttachmentScanProjection>> ClaimAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AttachmentScanProjectionService>();
        return await service.ClaimDueAsync(maxCount, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LeaseRenewalResult> RenewAsync(
        AttachmentScanProjection job,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AttachmentScanProjectionService>();
        return await service.RenewLeaseAsync(
                job.Id,
                job.LeaseOwner!,
                job.LeaseToken!,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> CompleteAsync(
        AttachmentScanProjection job,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AttachmentScanProjectionService>();
        return await service.CompleteClaimedAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RetryAsync(
        AttachmentScanProjection job,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AttachmentScanProjectionService>();
        return await service.RetryClaimedAsync(job, error, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeadLetterAsync(
        AttachmentScanProjection job,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AttachmentScanProjectionService>();
        return await service.DeadLetterClaimedAsync(job, error, cancellationToken).ConfigureAwait(false);
    }
}
