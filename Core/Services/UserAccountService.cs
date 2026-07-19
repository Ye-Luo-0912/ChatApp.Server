using Core.Exceptions;
using Core.Interfaces;
using Core.Models.Auth;
using Core.Models.User;
using Microsoft.Extensions.Logging;

namespace Core.Services;

/// <summary>
/// 处理用户资料查询、更新、删除和密码修改。
/// </summary>
public class UserAccountService(
    IUserRepository userRepository,
    IPasswordHasher passwordHasher,
    ILogger<UserAccountService> logger) : IUserAccountService
{
    /// <summary>
    /// 获取完整的用户资料。
    /// </summary>
    public async Task<UserProfileResponse?> GetByIdAsync(long userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await userRepository.FindByIdAsync(userId, cancellationToken);
            return user is null ? null : UserProfileResponse.FromUser(user);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "查找用户 ID {UserId} 时发生异常", userId);
            throw new IdentityException("用户查询失败", ex);
        }
    }

    /// <summary>
    /// 获取公开展示的用户信息。
    /// </summary>
    public async Task<PublicUserResponse?> GetByUserNameAsync(string username, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await userRepository.FindByNameAsync(username, cancellationToken);
            return user is null ? null : PublicUserResponse.FromUser(user);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "查找用户 {Username} 时发生异常", username);
            throw new IdentityException("用户查询失败", ex);
        }
    }

    /// <summary>
    /// 更新用户的邮箱和手机号等基础资料。
    /// </summary>
    public async Task<AuthOperationResult?> UpdateAsync(long userId, string? email, string? phoneNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await userRepository.FindByIdAsync(userId, cancellationToken);
            if (user is null)
                return null;

            if (email is not null)
            {
                user.Email = email;
                user.NormalizedEmail = email.Trim().ToUpperInvariant();
            }

            user.PhoneNumber = phoneNumber ?? user.PhoneNumber;

            var ok = await userRepository.UpdateAsync(user, cancellationToken);
            if (ok) logger.LogInformation("成功更新用户 {UserId}", userId);

            return ok
                ? AuthOperationResult.Success()
                : AuthOperationResult.Fail("UpdateFailed", "用户信息更新失败");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "更新用户 {UserId} 时发生异常", userId);
            throw new IdentityException("用户更新失败", ex);
        }
    }

    /// <summary>
    /// 删除指定用户。
    /// </summary>
    public async Task<AuthOperationResult?> DeleteAsync(long userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await userRepository.FindByIdAsync(userId, cancellationToken);
            if (user is null)
                return null;

            var ok = await userRepository.DeleteAsync(user, cancellationToken);
            if (ok) logger.LogInformation("成功删除用户 {UserId}", userId);

            return ok
                ? AuthOperationResult.Success()
                : AuthOperationResult.Fail("DeleteFailed", "用户删除失败");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "删除用户 {UserId} 时发生异常", userId);
            throw new IdentityException("用户删除失败", ex);
        }
    }

    /// <summary>
    /// 修改指定用户密码。
    /// </summary>
    public async Task<AuthOperationResult?> ChangePasswordAsync(long userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await userRepository.FindByIdAsync(userId, cancellationToken);
            if (user is null)
                return null;

            if (string.IsNullOrEmpty(user.PasswordHash)
                || !passwordHasher.VerifyPassword(currentPassword, user.PasswordHash))
                return AuthOperationResult.Fail("PasswordMismatch", "当前密码不正确");

            user.PasswordHash = passwordHasher.HashPassword(newPassword);
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.AccessFailedCount = 0;

            var ok = await userRepository.UpdateAsync(user, cancellationToken);
            if (ok) logger.LogInformation("用户 {UserId} 密码修改成功", userId);

            return ok
                ? AuthOperationResult.Success()
                : AuthOperationResult.Fail("UpdateFailed", "密码修改失败");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "修改用户 {UserId} 密码时发生异常", userId);
            throw new IdentityException("密码修改失败", ex);
        }
    }

}
