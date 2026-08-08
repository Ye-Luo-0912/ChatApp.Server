namespace Core.Interfaces;

/// <summary>
/// Presence 批量授权边界。实现必须在关系/跨库查询失败时返回空授权集合，不能 fail-open。
/// </summary>
public interface IPresenceAuthorizationService
{
    Task<IReadOnlySet<long>> AuthorizeAsync(
        long watcherUserId,
        IReadOnlyList<long> targetUserIds,
        CancellationToken cancellationToken = default);
}
