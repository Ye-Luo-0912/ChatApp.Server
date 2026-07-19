using Core.Models.Identity;

namespace Core.Interfaces;

/// <summary>
/// 用户数据访问抽象。
/// </summary>
public interface IUserRepository
{
    Task<ApplicationUser?> FindByIdAsync(long userId);
    Task<ApplicationUser?> FindByNameAsync(string username);
    Task<bool> UpdateAsync(ApplicationUser user);
    Task<bool> DeleteAsync(ApplicationUser user);
}
