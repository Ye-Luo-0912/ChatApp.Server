using Core.Models.Identity;

namespace Core.Interfaces;

/// <summary>
/// 用户数据访问抽象。
/// </summary>
public interface IUserRepository
{
    Task<ApplicationUser?> FindByIdAsync(long userId, CancellationToken cancellationToken = default);
    Task<ApplicationUser?> FindByNameAsync(string username, CancellationToken cancellationToken = default);
    Task<bool> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken = default);
}
