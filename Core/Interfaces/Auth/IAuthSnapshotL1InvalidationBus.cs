namespace Core.Interfaces.Auth;

/// <summary>
/// 用户认证 fence L1 的跨实例失效通知总线。
/// <para>
/// Pub/Sub 只负责降低撤销传播延迟，不承担持久化语义；消息丢失时，
/// Auth Fence 的短 TTL 与持久化 SecurityVersion 仍然保证最终收敛。
/// </para>
/// </summary>
public interface IAuthSnapshotL1InvalidationBus
{
    void Register(Action<long, long?> evict);
    void Publish(long userId, long? minimumSecurityVersion = null);
}
