using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;

namespace ChatApp.Server.Middlewares;

/// <summary>
/// 在读取请求体前根据 Content-Length 拒绝超限请求（413）。
/// TestServer 不完整支持 Kestrel IHttpMaxRequestBodySizeFeature，因此显式兜底。
/// <para>
/// 限制来源优先级：端点元数据 [RequestSizeLimit] → IHttpMaxRequestBodySizeFeature → 宿主回退上限。
/// 不再向下覆盖 feature.MaxRequestBodySize，使端点级 [RequestSizeLimit] 能真正生效。
/// </para>
/// </summary>
public sealed class RequestBodySizeLimitMiddleware(RequestDelegate next, long fallbackMaxBytes)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var limit = ResolveLimit(context);

        if (limit is > 0)
        {
            var contentLength = context.Request.ContentLength;
            if (contentLength is > 0 && contentLength > limit)
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
        }

        await next(context);
    }

    /// <summary>
    /// 解析当前请求的有效请求体上限。
    /// 端点 [RequestSizeLimit] 优先；其次 Kestrel 全局 feature；最后回退到宿主安全上限。
    /// </summary>
    private long? ResolveLimit(HttpContext context)
    {
        // 接口属性同时表达 [DisableRequestSizeLimit] 的 null（无限制）语义。
        var endpointMetadata = context.GetEndpoint()?
            .Metadata.GetMetadata<IRequestSizeLimitMetadata>();
        if (endpointMetadata is not null)
            return endpointMetadata.MaxRequestBodySize;

        // Kestrel 全局或框架已设置的请求级上限
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature?.MaxRequestBodySize is { } featureLimit)
            return featureLimit;

        // TestServer 等无 feature 场景的兜底
        return fallbackMaxBytes;
    }
}
