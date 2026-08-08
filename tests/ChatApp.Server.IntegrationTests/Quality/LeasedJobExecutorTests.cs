using Core.Interfaces;
using Core.Models.Export;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Quality;

public sealed class LeasedJobExecutorTests
{
    [Fact]
    public async Task LeaseLoss_CancelsExternalWork_AndSkipsTerminalWrite()
    {
        using var concurrency = new WorkerConcurrencyManager(
            Options.Create(new Core.Settings.WorkerConcurrencyOptions
            {
                GlobalMaxConcurrency = 1,
            }));
        var store = new FakeStore(new TestJob(1));
        var executor = new LeasedJobExecutor<TestJob>(
            concurrency,
            NullLogger<LeasedJobExecutor<TestJob>>.Instance);
        var canceled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var completed = await executor.DrainAsync(
            "test_lease_loss",
            workerMaxConcurrency: 1,
            leaseDuration: TimeSpan.FromSeconds(3),
            store,
            async (_, cancellationToken) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    canceled.TrySetResult(true);
                    throw;
                }
            },
            _ => false);

        Assert.True(await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, completed);
        Assert.Equal(0, store.CompleteCount);
        Assert.Equal(0, store.RetryCount);
        Assert.Equal(1, store.RenewCount);
    }

    [Fact]
    public async Task TransientRenewalFailure_CancelsExternalWork_AndSkipsTerminalWrite()
    {
        using var concurrency = new WorkerConcurrencyManager(
            Options.Create(new Core.Settings.WorkerConcurrencyOptions
            {
                GlobalMaxConcurrency = 1,
            }));
        var store = new FakeStore(new TestJob(11))
        {
            RenewalResult = LeaseRenewalResult.TransientFailure,
        };
        var executor = new LeasedJobExecutor<TestJob>(
            concurrency,
            NullLogger<LeasedJobExecutor<TestJob>>.Instance);
        var canceled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var completed = await executor.DrainAsync(
            "test_lease_uncertain",
            workerMaxConcurrency: 1,
            leaseDuration: TimeSpan.FromSeconds(3),
            store,
            async (_, cancellationToken) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    canceled.TrySetResult(true);
                    throw;
                }
            },
            _ => false);

        Assert.True(await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, completed);
        Assert.Equal(0, store.CompleteCount);
        Assert.Equal(0, store.RetryCount);
        Assert.Equal(1, store.RenewCount);
    }

    [Fact]
    public async Task EmptyClaim_ReleasesReservation_ForNextDrain()
    {
        using var concurrency = new WorkerConcurrencyManager(
            Options.Create(new Core.Settings.WorkerConcurrencyOptions
            {
                GlobalMaxConcurrency = 1,
            }));
        var store = new FakeStore();
        var executor = new LeasedJobExecutor<TestJob>(
            concurrency,
            NullLogger<LeasedJobExecutor<TestJob>>.Instance);

        Assert.Equal(
            0,
            await executor.DrainAsync(
                "test_empty_claim",
                workerMaxConcurrency: 1,
                leaseDuration: TimeSpan.FromSeconds(3),
                store,
                (_, _) => Task.CompletedTask,
                _ => false));

        store.Add(new TestJob(2));
        Assert.Equal(
            1,
            await executor.DrainAsync(
                "test_empty_claim",
                workerMaxConcurrency: 1,
                leaseDuration: TimeSpan.FromSeconds(3),
                store,
                (_, _) => Task.CompletedTask,
                _ => false));

        Assert.Equal(1, store.CompleteCount);
    }

    [Fact]
    public async Task ExecutionFailure_RetriesWithFence_AndIsNotCountedAsCompleted()
    {
        using var concurrency = new WorkerConcurrencyManager(
            Options.Create(new Core.Settings.WorkerConcurrencyOptions
            {
                GlobalMaxConcurrency = 1,
            }));
        var store = new FakeStore(new TestJob(3));
        var executor = new LeasedJobExecutor<TestJob>(
            concurrency,
            NullLogger<LeasedJobExecutor<TestJob>>.Instance);

        var completed = await executor.DrainAsync(
            "test_retry",
            1,
            TimeSpan.FromSeconds(3),
            store,
            (_, _) => Task.FromException(new InvalidOperationException("injected failure")),
            _ => false);

        Assert.Equal(0, completed);
        Assert.Equal(0, store.CompleteCount);
        Assert.Equal(1, store.RetryCount);
        Assert.Equal(0, store.DeadLetterCount);
    }

    [Fact]
    public async Task ExecutionFailure_DeadLetters_AndCountsTheTerminalJob()
    {
        using var concurrency = new WorkerConcurrencyManager(
            Options.Create(new Core.Settings.WorkerConcurrencyOptions
            {
                GlobalMaxConcurrency = 1,
            }));
        var store = new FakeStore(new TestJob(4));
        var executor = new LeasedJobExecutor<TestJob>(
            concurrency,
            NullLogger<LeasedJobExecutor<TestJob>>.Instance);

        var completed = await executor.DrainAsync(
            "test_dead_letter",
            1,
            TimeSpan.FromSeconds(3),
            store,
            (_, _) => Task.FromException(new InvalidOperationException("injected failure")),
            _ => true);

        Assert.Equal(1, completed);
        Assert.Equal(0, store.CompleteCount);
        Assert.Equal(0, store.RetryCount);
        Assert.Equal(1, store.DeadLetterCount);
    }

    [Fact]
    public async Task ClaimNeverExceedsReservedExecutionCapacity()
    {
        using var concurrency = new WorkerConcurrencyManager(
            Options.Create(new Core.Settings.WorkerConcurrencyOptions
            {
                GlobalMaxConcurrency = 2,
            }));
        var store = new FakeStore(Enumerable.Range(1, 5).Select(id => new TestJob(id)).ToArray());
        var executor = new LeasedJobExecutor<TestJob>(
            concurrency,
            NullLogger<LeasedJobExecutor<TestJob>>.Instance);

        var completed = await executor.DrainAsync(
            "test_capacity",
            2,
            TimeSpan.FromSeconds(3),
            store,
            async (_, cancellationToken) =>
                await Task.Delay(10, cancellationToken),
            _ => false);

        Assert.Equal(5, completed);
        Assert.Equal(5, store.CompleteCount);
        Assert.InRange(store.MaxClaimCount, 0, 2);
    }

    private sealed record TestJob(long Id);

    private sealed class FakeStore(params TestJob[] initial) : ILeasedJobStore<TestJob>
    {
        private readonly Queue<TestJob> _jobs = new(initial);
        private readonly object _gate = new();

        private int _completeCount;
        private int _retryCount;
        private int _renewCount;
        private int _deadLetterCount;

        public LeaseRenewalResult RenewalResult { get; set; } = LeaseRenewalResult.LeaseLost;

        public int CompleteCount => Volatile.Read(ref _completeCount);
        public int RetryCount => Volatile.Read(ref _retryCount);
        public int RenewCount => Volatile.Read(ref _renewCount);
        public int DeadLetterCount => Volatile.Read(ref _deadLetterCount);
        public int MaxClaimCount { get; private set; }

        public void Add(TestJob job)
        {
            lock (_gate)
                _jobs.Enqueue(job);
        }

        public Task<IReadOnlyList<TestJob>> ClaimAsync(
            int maxCount,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                MaxClaimCount = Math.Max(MaxClaimCount, maxCount);
                var claimed = new List<TestJob>(Math.Min(maxCount, _jobs.Count));
                while (claimed.Count < maxCount && _jobs.Count > 0)
                    claimed.Add(_jobs.Dequeue());
                return Task.FromResult<IReadOnlyList<TestJob>>(claimed);
            }
        }

        public Task<LeaseRenewalResult> RenewAsync(
            TestJob job,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _renewCount);
            return Task.FromResult(RenewalResult);
        }

        public Task<bool> CompleteAsync(
            TestJob job,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _completeCount);
            return Task.FromResult(true);
        }

        public Task<bool> RetryAsync(
            TestJob job,
            string error,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _retryCount);
            return Task.FromResult(true);
        }

        public Task<bool> DeadLetterAsync(
            TestJob job,
            string error,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _deadLetterCount);
            return Task.FromResult(true);
        }
    }
}
