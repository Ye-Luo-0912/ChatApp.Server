using Core.Caching;

namespace Core.Interfaces.Cache;

/// <summary>
/// 一次性状态存储：用于上传票、下载票、验证码、Step-up、MFA 挑战。
/// </summary>
/// <remarks>
/// <para>固定策略：</para>
/// <list type="bullet">
///   <item>Garnet 故障：失败关闭（fail-closed），抛出异常。</item>
///   <item>原子消费行为集中实现：并发调用同一键时至多一个成功。</item>
///   <item>恢复不能延长原始截止时间。</item>
///   <item>业务服务不再直接组合 ICacheValueStore + IAtomicCacheStore。</item>
/// </list>
/// </remarks>
public interface IOneTimeStateStore
{
    /// <summary>签发一次性状态，在 expiresAt 之前有效。</summary>
    Task IssueAsync<T>(
        string key,
        T payload,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 原子消费一次性状态（GETDEL 语义）。
    /// 并发调用同一键时至多一个调用者获得载荷。
    /// </summary>
    Task<T?> TryConsumeAsync<T>(
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically moves a value to a durable claim key. A pre-existing claim
    /// blocks a second claimant until it is completed or the original expiry
    /// is reached.
    /// </summary>
    Task<OneTimeStateClaim<T>?> TryClaimAsync<T>(
        string key,
        string claimKey,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteClaimAsync(
        string claimKey,
        CancellationToken cancellationToken = default);

    /// <summary>Moves an uncompleted claim back to the original key.</summary>
    Task<bool> RestoreClaimAsync(
        string key,
        string claimKey,
        DateTimeOffset originalExpiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 恢复一次性状态（业务处理失败后回滚）。
    /// TTL = expiresAt - 当前时间；若已过期则不恢复。
    /// </summary>
    Task RestoreAsync<T>(
        string key,
        T payload,
        DateTimeOffset originalExpiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>仅当当前值等于 expectedValue 时原子消费（CAS-DELETE）。</summary>
    Task<bool> TryConsumeIfEqualAsync(
        string key,
        string expectedValue,
        CancellationToken cancellationToken = default);

    /// <summary>读取但不消费（用于 Step-up 先读取载荷做绑定校验）。</summary>
    Task<string?> PeekAsync(
        string key,
        CancellationToken cancellationToken = default);
}
