namespace Core.Caching;

/// <summary>
/// 描述一次原子写入中的单个缓存条目。
/// </summary>
public sealed class CacheSetRequest
{
    public required string Key { get; init; }

    public required object Value { get; init; }

    public TimeSpan? AbsoluteExpiration { get; init; }

    public TimeSpan? SlidingExpiration { get; init; }

    /// <summary>
    /// 为 <see langword="true"/> 时使用 Redis STRING（单次 GET/SET），适用于访问令牌热路径。
    /// </summary>
    public bool AsRedisString { get; init; }
}
