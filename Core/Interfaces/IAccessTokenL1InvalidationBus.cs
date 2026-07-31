namespace Core.Interfaces;

/// <summary>
/// 访问令牌 L1 缓存的跨实例失效通知总线。
/// <para>通知是性能优化，不是持久化语义；总线故障时由 L1 TTL 保证最终收敛。</para>
/// </summary>
public interface IAccessTokenL1InvalidationBus
{
    void Register(Action<string> evict);
    void Publish(string accessTokenKey);
}
