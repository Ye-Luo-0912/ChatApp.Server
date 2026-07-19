namespace Core.Caching;

/// <summary>
/// <see cref="Interfaces.Cache.ICacheProvider.TryAtomicConsumeAsync{T,TResult}"/> 的执行结果。
/// </summary>
public readonly struct AtomicConsumeResult<TResult>
{
    public bool Succeeded { get; private init; }

    public TResult Value { get; private init; }

    public static AtomicConsumeResult<TResult> Ok(TResult value) => new()
    {
        Succeeded = true,
        Value = value,
    };

    public static AtomicConsumeResult<TResult> Fail() => new()
    {
        Succeeded = false,
        Value = default!,
    };
}
