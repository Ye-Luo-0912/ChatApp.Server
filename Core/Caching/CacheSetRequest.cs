namespace Core.Caching;

/// <summary>
/// 描述一次原子写入中的单个缓存条目。
/// </summary>
public sealed class CacheSetRequest
{
    public required string Key { get; init; }

    public required object Value { get; init; }

    public required TimeSpan Expiration { get; init; }
}
