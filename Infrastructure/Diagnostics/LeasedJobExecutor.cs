using Core.Interfaces;
using Core.Models.Export;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Diagnostics;

/// <summary>
/// Shared reservation/claim/heartbeat/fenced-finalization loop for durable
/// leased jobs. The store is deliberately supplied by the caller so a store
/// can use a fresh DbContext for every operation when the worker runs jobs in
/// parallel.
/// </summary>
public sealed class LeasedJobExecutor<TJob>(
    WorkerConcurrencyManager concurrencyManager,
    ILogger<LeasedJobExecutor<TJob>> logger)
{
    /// <summary>
    /// Drains the currently due queue until no more work can be claimed. The
    /// caller owns the outer backoff; this method owns all in-flight tasks and
    /// reservations before returning.
    /// </summary>
    public async Task<int> DrainAsync(
        string workerName,
        int workerMaxConcurrency,
        TimeSpan leaseDuration,
        ILeasedJobStore<TJob> store,
        Func<TJob, CancellationToken, Task> executeAsync,
        Func<TJob, bool> shouldDeadLetter,
        CancellationToken cancellationToken = default)
        => await DrainAsync(
                workerName,
                workerMaxConcurrency,
                leaseDuration,
                store,
                async (job, token) =>
                {
                    await executeAsync(job, token).ConfigureAwait(false);
                    return LeasedJobExecutionOutcome.ExecuteAndFinalize;
                },
                shouldDeadLetter,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<int> DrainAsync(
        string workerName,
        int workerMaxConcurrency,
        TimeSpan leaseDuration,
        ILeasedJobStore<TJob> store,
        Func<TJob, CancellationToken, Task<LeasedJobExecutionOutcome>> executeAsync,
        Func<TJob, bool> shouldDeadLetter,
        CancellationToken cancellationToken = default)
    {
        workerMaxConcurrency = Math.Max(1, workerMaxConcurrency);
        leaseDuration = leaseDuration <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(2)
            : leaseDuration;

        var inFlight = new List<Task<int>>();
        var completed = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                for (var i = inFlight.Count - 1; i >= 0; i--)
                {
                    if (!inFlight[i].IsCompleted)
                        continue;

                    var finished = inFlight[i];
                    inFlight.RemoveAt(i);
                    completed += await finished.ConfigureAwait(false);
                }
                var available = Math.Max(0, workerMaxConcurrency - inFlight.Count);
                if (available == 0)
                {
                    await Task.WhenAny(inFlight).ConfigureAwait(false);
                    continue;
                }

                var reservations = new List<IAsyncDisposable>(available);
                while (reservations.Count < available
                       && concurrencyManager.TryAcquire(
                           workerName,
                           workerMaxConcurrency,
                           out var reservation))
                {
                    reservations.Add(reservation!);
                }

                if (reservations.Count == 0)
                {
                    if (inFlight.Count == 0)
                        break;

                    await Task.WhenAny(inFlight).ConfigureAwait(false);
                    continue;
                }

                IReadOnlyList<TJob> claimed;
                try
                {
                    claimed = await store
                        .ClaimAsync(reservations.Count, cancellationToken)
                        .ConfigureAwait(false);
                    if (store is IReclaimCountSource reclaimSource)
                    {
                        var reclaimed = reclaimSource.ConsumeReclaimedCount();
                        if (reclaimed > 0)
                            concurrencyManager.RecordReclaimed(workerName, reclaimed);
                    }
                }
                catch
                {
                    foreach (var reservation in reservations)
                        await reservation.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                if (claimed.Count > reservations.Count)
                {
                    foreach (var reservation in reservations)
                        await reservation.DisposeAsync().ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"{workerName} store claimed {claimed.Count} jobs for {reservations.Count} reservations");
                }

                for (var i = 0; i < claimed.Count; i++)
                {
                    var reservation = reservations[i];
                    inFlight.Add(ProcessOneAsync(
                        workerName,
                        claimed[i],
                        reservation,
                        leaseDuration,
                        store,
                        executeAsync,
                        shouldDeadLetter,
                        cancellationToken));
                }

                for (var i = claimed.Count; i < reservations.Count; i++)
                    await reservations[i].DisposeAsync().ConfigureAwait(false);

                if (claimed.Count == 0)
                {
                    if (inFlight.Count == 0)
                        break;

                    await Task.WhenAny(inFlight).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try
            {
                while (inFlight.Count > 0)
                {
                    var finished = await Task.WhenAny(inFlight).ConfigureAwait(false);
                    inFlight.Remove(finished);
                    completed += await finished.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The claimed rows retain their lease and are reclaimable after
                // expiry. Do not attempt an ambiguous terminal update here.
            }
        }

        return completed;
    }

    private async Task<int> ProcessOneAsync(
        string workerName,
        TJob job,
        IAsyncDisposable reservation,
        TimeSpan leaseDuration,
        ILeasedJobStore<TJob> store,
        Func<TJob, CancellationToken, Task<LeasedJobExecutionOutcome>> executeAsync,
        Func<TJob, bool> shouldDeadLetter,
        CancellationToken cancellationToken)
    {
        using var workCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseLost = 0;
        var terminal = 0;

        void CancelForLeaseLoss(bool ownershipLossConfirmed)
        {
            if (Interlocked.Exchange(ref leaseLost, 1) != 0)
                return;

            if (ownershipLossConfirmed)
                concurrencyManager.RecordLeaseLost(workerName);
            else
                concurrencyManager.RecordLeaseUncertain(workerName);
            workCts.Cancel();
        }

        var heartbeatInterval = TimeSpan.FromTicks(
            Math.Max(TimeSpan.FromSeconds(1).Ticks, leaseDuration.Ticks / 3));
        var heartbeat = Task.Run(async () =>
        {
            while (!heartbeatCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(heartbeatInterval, heartbeatCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (heartbeatCts.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    var renewed = await store
                        .RenewAsync(job, heartbeatCts.Token)
                        .ConfigureAwait(false);
                    if (renewed == LeaseRenewalResult.LeaseLost)
                    {
                        CancelForLeaseLoss(ownershipLossConfirmed: true);
                        heartbeatCts.Cancel();
                        return;
                    }

                    if (renewed == LeaseRenewalResult.TransientFailure)
                    {
                        concurrencyManager.RecordHeartbeatFailure(workerName);
                        logger.LogDebug(
                            "{Worker} lease renewal is uncertain; canceling active work",
                            workerName);
                        // A failed renewal does not prove that another owner
                        // has taken the row, but it also cannot prove that the
                        // lease is still ours. Stop before any further external
                        // side effect; the durable lease will be reclaimed by
                        // expiry and the job retried.
                        CancelForLeaseLoss(ownershipLossConfirmed: false);
                        heartbeatCts.Cancel();
                        return;
                    }
                }
                catch (OperationCanceledException)
                    when (heartbeatCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    concurrencyManager.RecordHeartbeatFailure(workerName);
                    logger.LogDebug(
                        ex,
                        "{Worker} lease renewal is uncertain; canceling active work",
                        workerName);
                    CancelForLeaseLoss(ownershipLossConfirmed: false);
                    heartbeatCts.Cancel();
                    return;
                }
            }
        }, CancellationToken.None);

        try
        {
            try
            {
                var outcome = await executeAsync(job, workCts.Token).ConfigureAwait(false);
                if (outcome == LeasedJobExecutionOutcome.LeaseLost)
                {
                    CancelForLeaseLoss(ownershipLossConfirmed: true);
                    return 0;
                }

                if (outcome == LeasedJobExecutionOutcome.RetryScheduled)
                    return 0;

                if (outcome == LeasedJobExecutionOutcome.AlreadyFinalized)
                {
                    if (Volatile.Read(ref leaseLost) != 0)
                        return 0;

                    concurrencyManager.RecordCompleted(workerName);
                    terminal = 1;
                    return terminal;
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested || Volatile.Read(ref leaseLost) != 0)
            {
                return 0;
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref leaseLost) != 0)
                    return 0;

                var finalized = shouldDeadLetter(job)
                    ? await store.DeadLetterAsync(job, Truncate(ex.Message), cancellationToken)
                        .ConfigureAwait(false)
                    : await store.RetryAsync(job, Truncate(ex.Message), cancellationToken)
                        .ConfigureAwait(false);
                if (!finalized)
                {
                    CancelForLeaseLoss(ownershipLossConfirmed: true);
                    return 0;
                }

                if (shouldDeadLetter(job))
                {
                    concurrencyManager.RecordCompleted(workerName);
                    terminal = 1;
                }
                return terminal;
            }

            if (Volatile.Read(ref leaseLost) != 0)
                return 0;

            var completed = await store.CompleteAsync(job, cancellationToken)
                .ConfigureAwait(false);
            if (!completed)
            {
                CancelForLeaseLoss(ownershipLossConfirmed: true);
                return 0;
            }

            concurrencyManager.RecordCompleted(workerName);
            terminal = 1;
            return terminal;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            // A failed fenced finalization is deliberately left leased. The
            // lease expiry makes the operation retryable without guessing
            // whether the previous database command committed.
            logger.LogWarning(ex, "{Worker} leased job finalization failed", workerName);
            return 0;
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.LogDebug(ex, "{Worker} heartbeat exited with error", workerName); }

            await reservation.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string Truncate(string value)
        => value.Length <= 500 ? value : value[..500];
}
