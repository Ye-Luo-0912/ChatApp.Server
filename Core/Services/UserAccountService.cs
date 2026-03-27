using Core.Exceptions;
using Core.Interfaces;
using Core.Models.DTOs.User;
using Core.Models.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Core.Services;

/// <summary>
/// 处理用户资料查询、更新、删除和密码修改。
/// </summary>
public class UserAccountService(
    UserManager<ApplicationUser> userManager,
    ILogger<UserAccountService> logger) : IUserAccountService
{
    /// <summary>
    /// 获取完整的用户资料。
    /// </summary>
    public async Task<UserProfileResponse?> GetByIdAsync(long userId)
    {
        try
        {
            var user = await userManager.FindByIdAsync(userId.ToString());
            return user is null ? null : MapProfile(user);
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
    public async Task<PublicUserResponse?> GetByUserNameAsync(string username)
    {
        try
        {
            var user = await userManager.FindByNameAsync(username);
            return user is null ? null : MapPublic(user);
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
    public async Task<IdentityResult?> UpdateAsync(long userId, string? email, string? phoneNumber)
    {
        try
        {
            var user = await userManager.FindByIdAsync(userId.ToString());
            if (user is null)
                return null;

            user.Email = email ?? user.Email;
            user.PhoneNumber = phoneNumber ?? user.PhoneNumber;

            var result = await userManager.UpdateAsync(user);
            if (result.Succeeded)
            {
                logger.LogInformation("成功更新用户 {UserId}", userId);
            }

            return result;
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
    public async Task<IdentityResult?> DeleteAsync(long userId)
    {
        try
        {
            var user = await userManager.FindByIdAsync(userId.ToString());
            if (user is null)
                return null;

            var result = await userManager.DeleteAsync(user);
            if (result.Succeeded)
            {
                logger.LogInformation("成功删除用户 {UserId}", userId);
            }

            return result;
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
    public async Task<IdentityResult?> ChangePasswordAsync(long userId, string currentPassword, string newPassword)
    {
        try
        {
            var user = await userManager.FindByIdAsync(userId.ToString());
            if (user is null)
                return null;

            var result = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
            if (!result.Succeeded)
                return result;

            await userManager.ResetAccessFailedCountAsync(user);
            logger.LogInformation("用户 {UserId} 密码修改成功", userId);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "修改用户 {UserId} 密码时发生异常", userId);
            throw new IdentityException("密码修改失败", ex);
        }
    }

    
    
    
    
    

    /// <summary>
    /// 将实体映射成完整用户资料响应。
    /// </summary>
    private static UserProfileResponse MapProfile(ApplicationUser user) => new()
    {
        Id = user.Id,
        UserName = user.UserName,
        Email = user.Email,
        EmailConfirmed = user.EmailConfirmed,
        PhoneNumber = user.PhoneNumber,
        AvatarUrl = user.AvatarUrl,
        Gender = user.Gender,
        Signature = user.Signature,
        Region = user.Region,
        Birthday = user.Birthday,
        Status = user.Status,
        CreatedDate = user.CreatedDate,
        LastLoginDate = user.LastLoginDate
    };

    /// <summary>
    /// 将实体映射成公开用户资料响应。
    /// </summary>
    private static PublicUserResponse MapPublic(ApplicationUser user) => new()
    {
        Id = user.Id,
        UserName = user.UserName,
        AvatarUrl = user.AvatarUrl,
        Signature = user.Signature,
        Status = user.Status
    };
}