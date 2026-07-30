namespace Core.Interfaces;

/// <summary>
/// 单例分布式限流器抽象：一次原子判定全部维度，不重试非幂等操作。
/// </summary>
/// <remarks>
/// 实现不应为每个分区键创建本地 <see cref="System.Threading.RateLimiting.RateLimiter"/> 对象，
/// 避免高基数 IP 下本地对象持续增长（DoS 面）。所有状态驻留在 Redis。
/// </remarks>
public interface IDistributedRateLimiter
{
    /// <summary>
    /// 在固定窗口内为全部分区维度尝试获取一个许可。
    /// 任一维度超限时拒绝，且不增加其他维度的计数。
    /// </summary>
    /// <param name="policyName">策略名；实现可用它保证 Redis Cluster 中的键位于同一 slot。</param>
    /// <param name="partitionKeys">同一策略的维度分区键，不含策略名。</param>
    /// <param name="permitLimit">窗口内最大许可数。</param>
    /// <param name="window">窗口时长。</param>
    /// <param name="failOpen">Redis 不可用时是否放行（<c>true</c>=放行，<c>false</c>=拒绝）。</param>
    /// <returns>是否放行，以及被拒时建议的 Retry-After。</returns>
    Task<RateLimitAcquireResult> TryAcquireAsync(
        string policyName,
        IReadOnlyList<string> partitionKeys,
        int permitLimit,
        TimeSpan window,
        bool failOpen,
        CancellationToken cancellationToken = default);
}

/// <summary>限流判定结果。</summary>
public readonly record struct RateLimitAcquireResult(bool Allowed, TimeSpan? RetryAfter);
