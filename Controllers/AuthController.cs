using ChatApp.Server.Models.Requests;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models;
using Core.Models.Auth;
using Core.Models.Token;
using Infrastructure.Models.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ChatApp.Server.Controllers;

/// <summary>
/// 处理登录、登出、注册和令牌续签等认证相关接口。
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController(IAuthService authService, ILogger<AuthController> logger, IEmailVerificationService emailVerificationService, IUserAccountService userAccountService) : BaseApiController
{

    private readonly IUserAccountService _userAccountService = userAccountService;
    private readonly IAuthService _authService = authService;
    private readonly IEmailVerificationService _emailVerificationService = emailVerificationService;
    
    
    /// <summary>
    /// 用户登录。
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest model)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);
            
        
        try
        {
            var result = await _authService.LoginAsync(model.Username, model.Password);
            return result.IsSuccess ? Ok(result) : BadRequest(result);
        }
        catch (IdentityException ex)
        {
            logger.LogError(ex, "登录失败: {Username}", model.Username);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message= "登录失败，请稍后再试" });
        }
    }

    /// <summary>
    /// 用户登出。
    /// </summary>
    /// <param name="request">包含刷新令牌的登出请求模型。</param>
    /// <returns>如果成功，返回包含成功消息的结果；否则返回错误信息。</returns>
    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return BadRequest(new { Message = "刷新令牌不能为空" });

        try
        {
            await _authService.LogoutAsync(userId, request.RefreshToken);
            return Ok(new { Message = "成功登出" });
        }
        catch (IdentityException ex)
        {
            logger.LogError(ex, "登出失败: {UserId}", userId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { ex.Message });
        }
    }

    /// <summary>
    /// 用户注册。
    /// </summary>
    /// <param name="model">包含用户名、电子邮件和密码的注册请求模型。</param>
    /// <param name="ct"></param>
    /// <returns>如果注册成功，返回创建的用户信息；否则返回错误信息。</returns>
    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);
        
        try
        {
            if (await _authService.IsEmailRegisteredAsync(model.Email))
                return BadRequest(new {Message="该邮箱已经被注册过了"});
            
            var verifyResult = await _emailVerificationService.VerifyEmailCodeAsync(model.Email, model.Code, EmailCodePurpose.Register, ct);
            if (!verifyResult.IsSuccess) 
                return BadRequest(new { Message = verifyResult.ErrorMessage });
            
            var result = await _authService.RegisterAsync(model.Username, model.Email, model.Password);

            
            return result.IsSuccess
                ? CreatedAtAction(
                    nameof(UsersController.GetUserByName),
                    "Users",
                    new { username = result.Username },
                    new
                    {
                        result.IsSuccess,result.UserId,
                        result.Username,result.Message
                    })
                : BadRequest(new {result.Errors, result.Message});
        }
        catch (IdentityException ex)
        {
            logger.LogError(ex, "注册失败: {Username}", model.Username);
            return StatusCode(StatusCodes.Status500InternalServerError, new { ex.Message });
        }
    }

    /// <summary>
    /// 使用刷新令牌获取新的访问令牌和刷新令牌。
    /// </summary>
    /// <param name="model">包含用户ID和刷新令牌的请求模型。</param>
    /// <returns>如果成功，返回包含新访问令牌和刷新令牌的结果；否则返回错误信息。</returns>
    [AllowAnonymous]
    [EnableRateLimiting("auth-refresh")]
    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest model)
    {
        if (string.IsNullOrWhiteSpace(model.RefreshToken) || model.UserId <= 0)
            return BadRequest(TokenPairResult.Fail(AuthErrorType.InvalidCredentials));

        try
        {
            var result = await _authService.RefreshLoginAsync(model.UserId, model.RefreshToken);

            if (!result.IsSuccess)
            {
                logger.LogInformation("刷新令牌失败 - 用户: {UserId}, 错误类型: {ErrorType}", model.UserId, result.ErrorType);
            }

            return result.IsSuccess ? Ok(result) : BadRequest(result);
        }
        catch (IdentityException ex)
        {
            logger.LogError(ex, "刷新令牌失败: {UserId}", model.UserId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { ex.Message });
        }
    }

    /// <summary>
    /// 发送注册验证码到指定邮箱。
    /// </summary>
    /// <param name="request">包含接收者电子邮件地址的请求对象。</param>
    /// <param name="ct">用于取消操作的令牌。</param>
    /// <returns>如果成功发送验证码，则返回Ok结果；否则，返回BadRequest并附带错误信息。</returns>
    [AllowAnonymous]
    [EnableRateLimiting("auth-email")]
    [HttpPost("send-register-code")]
    public async Task<IActionResult> SendRegisterCode([FromBody] SendEmailCodeRequest request, CancellationToken ct)
    {
        var email = request.Email;
        
        if(string.IsNullOrEmpty(email))
            return BadRequest(new { Message = "邮箱不能为空" });

        if (await _authService.IsEmailRegisteredAsync(email))
            return BadRequest("该邮箱已经被注册");
        
        var result= await _emailVerificationService.SendEmailCodeAsync(email, EmailCodePurpose.Register, ct);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
    
}