namespace Infrastructure.Diagnostics;

/// <summary>Per-request mutable state used by <see cref="DbCommandCounterInterceptor"/>.</summary>
public sealed class DbRequestCommandCounter
{
    private long _count;
    private long _authFenceCount;
    private double _poolWaitMilliseconds;

    public long Count => Interlocked.Read(ref _count);

    public long AuthFenceCount => Interlocked.Read(ref _authFenceCount);

    public double PoolWaitMilliseconds => Volatile.Read(ref _poolWaitMilliseconds);

    public void Increment(bool authFence = false)
    {
        Interlocked.Increment(ref _count);
        if (authFence)
            Interlocked.Increment(ref _authFenceCount);
    }

    public void AddPoolWait(double milliseconds)
        => Interlocked.Exchange(
            ref _poolWaitMilliseconds,
            Volatile.Read(ref _poolWaitMilliseconds) + Math.Max(0, milliseconds));
}
