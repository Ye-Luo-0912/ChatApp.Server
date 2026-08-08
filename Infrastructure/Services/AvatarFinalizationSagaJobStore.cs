using Core.Interfaces;
using Core.Models.Export;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Services;

public sealed class AvatarFinalizationSagaJobStore(
    IServiceScopeFactory scopeFactory) : ILeasedJobStore<AvatarFinalizationSaga>
{
    public async Task<IReadOnlyList<AvatarFinalizationSaga>> ClaimAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IAvatarFinalizationSagaService>()
            .ClaimDueAsync(maxCount, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LeaseRenewalResult> RenewAsync(
        AvatarFinalizationSaga job,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IAvatarFinalizationSagaService>()
            .RenewLeaseAsync(job, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> CompleteAsync(
        AvatarFinalizationSaga job,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IAvatarFinalizationSagaService>()
            .CompleteClaimedAsync(job, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> RetryAsync(
        AvatarFinalizationSaga job,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IAvatarFinalizationSagaService>()
            .RetryClaimedAsync(job, error, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> DeadLetterAsync(
        AvatarFinalizationSaga job,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IAvatarFinalizationSagaService>()
            .DeadLetterClaimedAsync(job, error, cancellationToken)
            .ConfigureAwait(false);
    }
}
