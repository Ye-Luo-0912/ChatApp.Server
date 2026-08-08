namespace Core.Interfaces.Auth;

/// <summary>
/// 用户认证安全版本的唯一推进边界。
/// 实现必须使用数据库原子自增并返回新版本，不能使用实体 read-modify-write。
/// </summary>
public interface ISecurityVersionAdvancer
{
    Task<long?> AdvanceSecurityVersionAsync(
        long userId,
        CancellationToken cancellationToken = default);
}
