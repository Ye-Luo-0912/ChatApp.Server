using System.Security.Cryptography;
using System.Text.Json;
using Core.Interfaces.Cache;
using Infrastructure.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ChatApp.Server.Filters;

/// <summary>
/// 分布式幂等：Redis SET NX 抢占 Processing（所有者令牌），完成后写入响应；仅缓存 2xx。
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class IdempotentAttribute : Attribute, IAsyncActionFilter
{
    public const string HeaderName = "X-Idempotency-Key";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProcessingTtl = TimeSpan.FromSeconds(30);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!HttpMethods.IsPost(context.HttpContext.Request.Method)
            && !HttpMethods.IsPut(context.HttpContext.Request.Method)
            && !HttpMethods.IsPatch(context.HttpContext.Request.Method))
        {
            await next();
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var keyValues)
            || string.IsNullOrWhiteSpace(keyValues))
        {
            await next();
            return;
        }

        var key = keyValues.ToString().Trim();
        if (key.Length > 128)
        {
            context.Result = new BadRequestObjectResult(new { Message = "Idempotency-Key 过长" });
            return;
        }

        var cache = context.HttpContext.RequestServices.GetService<ICacheProvider>();
        if (cache is null)
        {
            await next();
            return;
        }

        context.HttpContext.Request.EnableBuffering();
        string bodyHash;
        await using (var ms = new MemoryStream())
        {
            await context.HttpContext.Request.Body.CopyToAsync(ms, context.HttpContext.RequestAborted);
            context.HttpContext.Request.Body.Position = 0;
            bodyHash = Convert.ToHexString(SHA256.HashData(ms.ToArray()));
        }

        var userId = context.HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                     ?? "anon";
        var cacheKey = $"idem:{userId}:{context.HttpContext.Request.Method}:{context.HttpContext.Request.Path}:{key}";
        var lockKey = cacheKey + ":lock";
        var ownerToken = Guid.NewGuid().ToString("N");

        var existing = await cache.GetStringPayloadAsync<IdemRecord>(cacheKey, context.HttpContext.RequestAborted);
        if (existing is not null)
        {
            if (!string.Equals(existing.BodyHash, bodyHash, StringComparison.Ordinal))
            {
                context.Result = new ConflictObjectResult(new { Message = "相同 Idempotency-Key 对应不同请求体" });
                return;
            }

            if (existing.Status == "completed" && existing.ResponseJson is not null)
            {
                context.HttpContext.Response.Headers["X-Idempotent-Replay"] = "true";
                context.Result = new ContentResult
                {
                    StatusCode = existing.StatusCode,
                    Content = existing.ResponseJson,
                    ContentType = "application/json",
                };
                return;
            }

            if (existing.Status == "processing")
            {
                context.Result = new ConflictObjectResult(new { Message = "相同请求正在处理中" });
                return;
            }
        }

        var processing = new IdemRecord
        {
            Status = "processing",
            BodyHash = bodyHash,
            StatusCode = 0,
            Owner = ownerToken,
        };

        var claimed = await cache.StringSetIfNotExistsAsync(
            lockKey, ownerToken, ProcessingTtl, context.HttpContext.RequestAborted);
        if (!claimed)
        {
            existing = await cache.GetStringPayloadAsync<IdemRecord>(cacheKey, context.HttpContext.RequestAborted);
            if (existing is { Status: "completed", ResponseJson: not null }
                && string.Equals(existing.BodyHash, bodyHash, StringComparison.Ordinal))
            {
                context.HttpContext.Response.Headers["X-Idempotent-Replay"] = "true";
                context.Result = new ContentResult
                {
                    StatusCode = existing.StatusCode,
                    Content = existing.ResponseJson,
                    ContentType = "application/json",
                };
                return;
            }

            context.Result = new ConflictObjectResult(new { Message = "相同请求正在处理中" });
            return;
        }

        await cache.SetStringPayloadAsync(cacheKey, processing, ProcessingTtl, context.HttpContext.RequestAborted);

        // 处理期间续租：仅所有者可刷新
        using var renewCts = CancellationTokenSource.CreateLinkedTokenSource(context.HttpContext.RequestAborted);
        var renewTask = RenewLockAsync(cache, lockKey, ownerToken, renewCts.Token);

        ActionExecutedContext executed;
        try
        {
            executed = await next();
        }
        finally
        {
            renewCts.Cancel();
            try { await renewTask; } catch { /* ignore */ }
        }

        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            await cache.RemoveAsync(cacheKey, context.HttpContext.RequestAborted);
            await cache.TryStringCompareAndDeleteAsync(lockKey, ownerToken, context.HttpContext.RequestAborted);
            return;
        }

        if (executed.Result is ObjectResult { Value: not null } obj)
        {
            var status = obj.StatusCode ?? StatusCodes.Status200OK;
            if (status is >= 200 and < 300)
            {
                var json = JsonSerializer.Serialize(obj.Value, AppJsonOptions.Default);
                await cache.SetStringPayloadAsync(cacheKey, new IdemRecord
                {
                    Status = "completed",
                    BodyHash = bodyHash,
                    StatusCode = status,
                    ResponseJson = json,
                    Owner = ownerToken,
                }, Ttl, context.HttpContext.RequestAborted);
            }
            else
            {
                await cache.RemoveAsync(cacheKey, context.HttpContext.RequestAborted);
            }
        }
        else
        {
            await cache.RemoveAsync(cacheKey, context.HttpContext.RequestAborted);
        }

        await cache.TryStringCompareAndDeleteAsync(lockKey, ownerToken, context.HttpContext.RequestAborted);
    }

    private static async Task RenewLockAsync(
        ICacheProvider cache, string lockKey, string ownerToken, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                var renewed = await cache.TryStringCompareAndExpireAsync(
                    lockKey, ownerToken, ProcessingTtl, cancellationToken);
                if (!renewed)
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    private sealed class IdemRecord
    {
        public string Status { get; set; } = "processing";
        public string BodyHash { get; set; } = "";
        public int StatusCode { get; set; }
        public string? ResponseJson { get; set; }
        public string? Owner { get; set; }
    }
}
