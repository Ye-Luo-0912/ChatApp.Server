using Microsoft.AspNetCore.Http.Features;

namespace ChatApp.Server.Middlewares;

/// <summary>
/// 在读取请求体前根据 Content-Length 拒绝超限请求（413）。
/// TestServer 不完整支持 Kestrel IHttpMaxRequestBodySizeFeature，因此显式兜底。
/// </summary>
public sealed class RequestBodySizeLimitMiddleware(RequestDelegate next, long maxBytes)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var contentLength = context.Request.ContentLength;
        if (contentLength is > 0 && contentLength > maxBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://httpstatuses.com/413",
                title = "请求体过大",
                status = 413,
                error = "payload_too_large",
                instance = context.Request.Path.Value,
            });
            return;
        }

        // 兼容真实 Kestrel：同步下调请求级上限
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = maxBytes;

        await next(context);
    }
}
