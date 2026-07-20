using ChatApp.Server.Models.Requests;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models;
using Core.Models.Auth;
using Core.Models.Token;
using Infrastructure.Models.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ChatApp.Server.Controllers;

/// <summary>
/// 处理登录、登出、注册和令牌续签等认证相关接口。
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController(
    IAuthService authService,
    IMfaService mfaService,
    ILogger<AuthController> logger,
    IEmailVerificationService emailVerificationService) : BaseApiController
{
    /// <summary>
    /// 用户登录。
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [RequestTimeout("auth")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var result = await authService.LoginAsync(model.Username, model.Password, cancellationToken);
            return result.IsSuccess ? Ok(result) : BadRequest(result);
        }
        catch (IdentityException ex)
        {
            logger.LogError(ex, "登录失败: {Username}", model.Username);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "登录失败，请稍后再试" });
        }
    }

    /// <summary>完成 MFA 挑战。</summary>
    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [RequestTimeout("auth")]
    [HttpPost("mfa/verify")]
    public async Task<IActionResult> VerifyMfa([FromBody] MfaVerifyRequest model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var result = await authService.VerifyMfaAsync(model.MfaToken, model.Code, cancellationToken);
            return result.IsSuccess ? Ok(result) : BadRequest(result);
        }
        catch (IdentityException ex)
        {
            logger.LogError(ex, "MFA 验证失败");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "MFA 验证失败" });
        }
    }

    /// <summary>
    /// 用户登出。
    /// </summary>
    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return BadRequest(new { Message = "刷新令牌不能为空" });

        try
        {
            await authService.LogoutAsync(userId, request.RefreshToken, cancellationToken);
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
    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            if (await authService.IsEmailRegisteredAsync(model.Email, ct))
                return BadRequest(new { Message = "该邮箱已经被注册过了" });

            var verifyResult = await emailVerificationService.VerifyEmailCodeAsync(
                model.Email, model.Code, EmailCodePurpose.Register, ct);
            if (!verifyResult.IsSuccess)
                return BadRequest(new { Message = verifyResult.ErrorMessage });

            var result = await authService.RegisterAsync(model.Username, model.Email, model.Password, ct);

            return result.IsSuccess
                ? CreatedAtAction(
                    nameof(UsersController.GetUserByName),
                    "Users",
                    new { username = result.Username },
                    new
                    {
                        result.IsSuccess,
                        result.UserId,
                        result.Username,
                        result.Message
                    })
                : BadRequest(new { result.Errors, result.Message });
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
    [AllowAnonymous]
    [EnableRateLimiting("auth-refresh")]
    [RequestTimeout("auth")]
    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model.RefreshToken) || model.UserId <= 0)
            return BadRequest(TokenPairResult.Fail(AuthErrorType.InvalidCredentials));

        try
        {
            var result = await authService.RefreshLoginAsync(model.UserId, model.RefreshToken, cancellationToken);

            if (!result.IsSuccess)
            {
                logger.LogInformation(
                    "刷新令牌失败 - 用户: {UserId}, 错误类型: {ErrorType}",
                    model.UserId, result.ErrorType);
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
    [AllowAnonymous]
    [EnableRateLimiting("auth-email")]
    [RequestTimeout("email")]
    [HttpPost("send-register-code")]
    public async Task<IActionResult> SendRegisterCode([FromBody] SendEmailCodeRequest request, CancellationToken ct)
    {
        var email = request.Email;

        if (string.IsNullOrEmpty(email))
            return BadRequest(new { Message = "邮箱不能为空" });

        if (await authService.IsEmailRegisteredAsync(email, ct))
            return BadRequest("该邮箱已经被注册");

        var result = await emailVerificationService.SendEmailCodeAsync(email, EmailCodePurpose.Register, ct);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// 发送密码重置验证码。
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("auth-email")]
    [RequestTimeout("email")]
    [HttpPost("send-reset-code")]
    public async Task<IActionResult> SendResetCode([FromBody] SendResetPasswordCodeRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // 无论邮箱是否存在都返回成功文案，避免枚举账户
        if (await authService.IsEmailRegisteredAsync(request.Email, ct))
        {
            var result = await emailVerificationService.SendEmailCodeAsync(
                request.Email, EmailCodePurpose.ResetPassword, ct);
            if (!result.IsSuccess)
                return BadRequest(result);
        }

        return Ok(new { Message = "若该邮箱已注册，验证码将很快送达" });
    }

    /// <summary>
    /// 使用邮箱验证码重置密码（一次性消费验证码，并撤销全部会话）。
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [RequestTimeout("auth")]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var result = await authService.ResetPasswordAsync(
                request.Email, request.Code, request.NewPassword, ct);
            return result.Succeeded
                ? Ok(new { Message = "密码已重置，请重新登录" })
                : BadRequest(result.Errors);
        }
        catch (IdentityException ex)
        {
            logger.LogError(ex, "重置密码失败: {Email}", request.Email);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "重置密码失败" });
        }
    }

    /// <summary>开始 MFA 设置，返回密钥、otpauth URI 与恢复码（仅展示一次）。需密码确认。</summary>
    [Authorize]
    [HttpPost("mfa/setup")]
    public async Task<IActionResult> BeginMfaSetup(
        [FromBody] MfaSetupRequest model, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var (sharedKey, otpAuthUri, recoveryCodes) =
                await mfaService.BeginSetupAsync(userId, model.Password, cancellationToken);
            return Ok(new { SharedKey = sharedKey, OtpAuthUri = otpAuthUri, RecoveryCodes = recoveryCodes });
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(new { Message = "密码验证失败" });
        }
    }

    /// <summary>用 TOTP 确认启用 MFA。</summary>
    [Authorize]
    [HttpPost("mfa/confirm")]
    public async Task<IActionResult> ConfirmMfa([FromBody] MfaCodeRequest model, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var result = await mfaService.ConfirmSetupAsync(userId, model.Code, cancellationToken);
        return result.Succeeded ? Ok(new { Message = "MFA 已启用" }) : BadRequest(result.Errors);
    }

    /// <summary>关闭 MFA（需密码 + TOTP 或恢复码）。</summary>
    [Authorize]
    [HttpPost("mfa/disable")]
    public async Task<IActionResult> DisableMfa([FromBody] MfaDisableRequest model, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var result = await mfaService.DisableAsync(userId, model.Password, model.Code, cancellationToken);
        return result.Succeeded ? Ok(new { Message = "MFA 已关闭" }) : BadRequest(result.Errors);
    }

    /// <summary>重新生成恢复码（旧码全部作废）。</summary>
    [Authorize]
    [HttpPost("mfa/recovery-codes/regenerate")]
    public async Task<IActionResult> RegenerateRecoveryCodes(
        [FromBody] MfaDisableRequest model, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized();
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var (result, codes) = await mfaService.RegenerateRecoveryCodesAsync(
            userId, model.Password, model.Code, cancellationToken);
        return result.Succeeded
            ? Ok(new { Message = "恢复码已重新生成", RecoveryCodes = codes })
            : BadRequest(result.Errors);
    }
}
