using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Core.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace ChatApp.Server.Middlewares
{
    /// <summary>
    /// 全局异常处理中间件（ProblemDetails）。
    /// </summary>
    public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                logger.LogInformation("请求被客户端取消: {Path}", context.Request.Path);
            }
            catch (ValidationException ex)
            {
                logger.LogWarning(ex, "业务验证错误");
                await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "validation_error", ex.Message);
            }
            catch (UnauthorizedAccessException)
            {
                await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, "unauthorized", "未授权访问");
            }
            catch (KeyNotFoundException)
            {
                await WriteProblemAsync(context, StatusCodes.Status404NotFound, "not_found", "资源不存在");
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "参数错误");
                await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "bad_request", ex.Message);
            }
            catch (CacheUnavailableException)
            {
                logger.LogError("缓存不可用");
                await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable, "cache_unavailable",
                    "服务暂时不可用，请稍后重试");
            }
            catch (PasswordVerifyOverloadedException)
            {
                logger.LogWarning("密码校验过载");
                await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable, "password_verify_overloaded",
                    "服务繁忙，请稍后重试");
            }
            catch (TimeoutException ex)
            {
                logger.LogWarning(ex, "操作超时");
                await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable, "timeout",
                    ex.Message);
            }
            catch (BadHttpRequestException ex)
            {
                var status = ex.StatusCode is >= 400 and < 600
                    ? ex.StatusCode
                    : StatusCodes.Status400BadRequest;
                var error = status == StatusCodes.Status413PayloadTooLarge
                    ? "payload_too_large"
                    : "bad_request";
                logger.LogWarning(ex, "错误的 HTTP 请求: {Status}", status);
                await WriteProblemAsync(context, status, error, ex.Message);
            }
            catch (Exception ex)
            {
                if (IsRequestBodyTooLarge(ex))
                {
                    await WriteProblemAsync(context, StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                        "请求体过大");
                    return;
                }

                logger.LogError(ex,
                    "未处理异常 | {Method} {Path} | User: {UserId}",
                    context.Request.Method,
                    context.Request.Path,
                    context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous");

                var includeDetail = context.RequestServices.GetService<IHostEnvironment>()?.IsEnvironment("Testing") == true
#if DEBUG
                                    || true
#endif
                    ;
                await WriteProblemAsync(context, StatusCodes.Status500InternalServerError, "internal_error",
                    "服务器内部错误",
                    includeDetail ? ex.ToString() : null);
            }
        }

        private static bool IsRequestBodyTooLarge(Exception ex)
        {
            for (var e = ex; e is not null; e = e.InnerException)
            {
                if (e is BadHttpRequestException bad && bad.StatusCode == StatusCodes.Status413PayloadTooLarge)
                    return true;
                if (e.Message.Contains("Request body too large", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static Task WriteProblemAsync(
            HttpContext context,
            int statusCode,
            string type,
            string title,
            string? detail = null)
        {
            if (context.Response.HasStarted)
                return Task.CompletedTask;

            var problem = new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Type = $"https://httpstatuses.com/{statusCode}",
                Detail = detail,
                Instance = context.Request.Path,
            };
            problem.Extensions["error"] = type;
            problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
            if (context.Request.Headers.TryGetValue(CorrelationIdMiddleware.HeaderName, out var cid))
                problem.Extensions["correlationId"] = cid.ToString();

            var payload = JsonSerializer.SerializeToUtf8Bytes(problem, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            });
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/problem+json";
            return context.Response.Body.WriteAsync(payload, context.RequestAborted).AsTask();
        }
    }
}
