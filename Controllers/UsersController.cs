using ChatApp.Server.Models.Requests;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.Server.Controllers;

/// <summary>
/// 处理用户资料和用户资源访问相关接口。
/// </summary>
[ApiController]
[Authorize]
[Route("api/users")]
public class UsersController(IUserAccountService userAccountService) : BaseApiController
{
    /// <summary>
    /// 获取当前登录用户资料。
    /// </summary>
    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentUser()
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var user = await userAccountService.GetByIdAsync(userId);
        return user is not null ? Ok(user) : NotFound();
    }

    /// <summary>
    /// 根据用户名查询公开用户信息。
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{username}")]
    public async Task<IActionResult> GetUserByName(string username)
    {
        var user = await userAccountService.GetByUserNameAsync(username);
        return user is not null
            ? Ok(user)
            : NotFound(new { Message = "用户不存在" });
    }

    /// <summary>
    /// 更新当前登录用户的基础资料。
    /// </summary>
    [HttpPut("me")]
    public async Task<IActionResult> UpdateCurrentUser([FromBody] UpdateCurrentUserRequest model)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var result = await userAccountService.UpdateAsync(userId, model.Email, model.PhoneNumber);
        if (result is null)
            return NotFound();

        return result.Succeeded ? Ok(new { Message = "更新成功" }) : BadRequest(result.Errors);
    }

    /// <summary>
    /// 修改当前登录用户密码。
    /// </summary>
    [HttpPost("me/change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest model)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        var result = await userAccountService.ChangePasswordAsync(userId, model.CurrentPassword, model.NewPassword);
        if (result is null)
            return NotFound();

        return result.Succeeded ? Ok(result) : BadRequest(result.Errors);
    }

    /// <summary>
    /// 管理员删除指定用户。
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpDelete("{userId:long}")]
    public async Task<IActionResult> DeleteUser(long userId)
    {
        var result = await userAccountService.DeleteAsync(userId);
        if (result is null)
            return NotFound();

        return result.Succeeded ? NoContent() : BadRequest(result.Errors);
    }
}