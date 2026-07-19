namespace Core.Caching;

/// <summary>
/// 原子消费计划：在确认旧键仍持有原值后，删除旧键并写入替换条目。
/// </summary>
/// <typeparam name="TResult">调用方希望在 CAS 成功后拿到的业务结果。</typeparam>
public sealed class AtomicConsumePlan<TResult>
{
    /// <summary>CAS 成功后返回给调用方的结果。</summary>
    public required TResult Result { get; init; }

    /// <summary>除被消费键以外，需要一并删除的键（例如旧访问令牌键）。</summary>
    public IReadOnlyList<string> AdditionalKeysToDelete { get; init; } = [];

    /// <summary>需要原子写入的新缓存条目。</summary>
    public IReadOnlyList<CacheSetRequest> Writes { get; init; } = [];
}
