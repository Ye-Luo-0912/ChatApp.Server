using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Claims;
using Core.Exceptions;

namespace ChatApp.Server.Middlewares
{
    /// <summary>
    /// 全局异常处理中间件
    /// </summary>
    public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

        private readonly ILogger<ExceptionHandlingMiddleware> _logger =
            logger ?? throw new ArgumentNullException(nameof(logger));

        /// <summary>
        /// 处理HTTP请求管道中的异常
        /// </summary>
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
                await HandleExceptionAsync(context, HttpStatusCode.BadRequest, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "未授权访问");
                await HandleExceptionAsync(context, HttpStatusCode.Unauthorized, ex);
            }
            catch (KeyNotFoundException ex)
            {
                logger.LogWarning(ex, "资源不存在");
                await HandleExceptionAsync(context, HttpStatusCode.NotFound, ex);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "参数错误");
                await HandleExceptionAsync(context, HttpStatusCode.BadRequest, ex);
            }
            catch (CacheUnavailableException ex)
            {
                logger.LogError(ex, "缓存不可用");
                await HandleExceptionAsync(context, HttpStatusCode.ServiceUnavailable, ex);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "未处理异常 | {Method} {Path} | User: {UserId}",
                    context.Request.Method,
                    context.Request.Path,
                    context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous");

                await HandleExceptionAsync(context, HttpStatusCode.InternalServerError, ex);
            }
        }

        /// <summary>
        /// 格式化异常响应并返回给客户端
        /// </summary>
        private static Task HandleExceptionAsync(
            HttpContext context,
            HttpStatusCode statusCode,
            Exception exception)
        {
            //响应是否已经开始发送
            if (context.Response.HasStarted) 
                return Task.CompletedTask;

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)statusCode;

            var response = new
            {
                error = (int)statusCode,
                message = statusCode switch
                {
                    HttpStatusCode.BadRequest => exception.Message,
                    HttpStatusCode.Unauthorized => "未授权访问",
                    HttpStatusCode.NotFound => "资源不存在",
                    HttpStatusCode.ServiceUnavailable => "服务暂时不可用，请稍后重试",
                    _ => "服务器内部错误"
                },
#if DEBUG
                detail = exception.ToString()
#endif
            };

            return context.Response.WriteAsJsonAsync(response);
        }
    }
}