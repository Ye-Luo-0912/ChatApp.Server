using Core.Models.Auth;
using Core.Models.User;

namespace Core.Interfaces;

/// <summary>
/// 定义用户账户资料模块的核心能力。
/// </summary>
public interface IUserAccountService
{
    /// <summary>
    /// 根据用户 ID 获取完整用户资料。
    /// </summary>
    Task<UserProfileResponse?> GetByIdAsync(long userId);

    /// <summary>
    /// 根据用户名获取公开用户信息。
    /// </summary>
    Task<PublicUserResponse?> GetByUserNameAsync(string username);

    /// <summary>
    /// 更新用户资料。
    /// </summary>
    Task<AuthOperationResult?> UpdateAsync(long userId, string? email, string? phoneNumber);

    /// <summary>
    /// 删除用户。
    /// </summary>
    Task<AuthOperationResult?> DeleteAsync(long userId);

    /// <summary>
    /// 修改用户密码。
    /// </summary>
    Task<AuthOperationResult?> ChangePasswordAsync(long userId, string currentPassword, string newPassword);
}