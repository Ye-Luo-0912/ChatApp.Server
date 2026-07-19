namespace Core.Caching;

/// <summary>
/// 描述一次原子写入中的单个缓存条目（与 <see cref="Interfaces.Cache.ICacheProvider.TryAtomicConsumeAsync{T,TResult}"/> 配合使用）。
/// </summary>
public sealed class CacheSetRequest
{
    public required string Key { get; init; }

    public required object Value { get; init; }

    public TimeSpan? AbsoluteExpiration { get; init; }

    public TimeSpan? SlidingExpiration { get; init; }
}
