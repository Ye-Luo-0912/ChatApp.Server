using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ChatApp.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public abstract class BaseApiController : ControllerBase, IActionFilter
{
    /// <summary>
    /// 尝试从当前登录用户的声明中解析用户 ID。
    /// </summary>
    protected bool TryGetCurrentUserId(out long userId)
    {
        var rawUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? User.FindFirstValue(ClaimTypes.Name)
                        ?? User.FindFirstValue("sub");

        return long.TryParse(rawUserId, out userId);
    }

    /// <summary>
    /// 执行 Action 前统一校验模型状态。
    /// </summary>
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!ModelState.IsValid)
        {
            context.Result = BadRequest(new ValidationProblemDetails(ModelState)
            {
                Status = StatusCodes.Status400BadRequest
            });
        }
    }

    /// <summary>
    /// 执行 Action 后不做额外处理，保留扩展点。
    /// </summary>
    public void OnActionExecuted(ActionExecutedContext context) { }
}