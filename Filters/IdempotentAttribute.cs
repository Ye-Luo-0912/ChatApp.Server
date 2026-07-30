using System.Security.Cryptography;
using System.Text.Json;
using Core.Interfaces.Cache;
using Infrastructure.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace ChatApp.Server.Filters;

/// <summary>
/// 单键幂等状态机：SET NX 抢占，CAS 续租/完成/放弃。仅缓存成功的 JSON ObjectResult。
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class IdempotentAttribute : Attribute, IAsyncResourceFilter
{
    public const string HeaderName = "X-Idempotency-Key";
    private static readonly TimeSpan CompletedTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProcessingTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(10);

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (!CanHaveBody(http.Request.Method)
            || !http.Request.Headers.TryGetValue(HeaderName, out var keyValues)
            || string.IsNullOrWhiteSpace(keyValues))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var idempotencyKey = keyValues.ToString().Trim();
        if (idempotencyKey.Length > 128)
        {
            context.Result = new BadRequestObjectResult(new { Message = "Idempotency-Key 过长" });
            return;
        }

        var bodyHash = await HashBodyAsync(http.Request, http.RequestAborted).ConfigureAwait(false);
        var userId = http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anon";
        var stateKey = $"idem:{userId}:{http.Request.Method}:{http.Request.Path}:{idempotencyKey}";
        var values = http.RequestServices.GetRequiredService<ICacheValueStore>();
        var atomic = http.RequestServices.GetRequiredService<IAtomicCacheStore>();

        var existing = Parse(await values.StringGetAsync(stateKey, http.RequestAborted).ConfigureAwait(false));
        if (TryReplayOrReject(context, existing, bodyHash))
            return;

        var processing = new IdemRecord
        {
            State = IdemState.Processing,
            BodyHash = bodyHash,
            Owner = Guid.NewGuid().ToString("N"),
        };
        var processingJson = JsonSerializer.Serialize(processing, AppJsonOptions.Default);

        var claimed = await atomic.StringSetIfNotExistsAsync(
            stateKey, processingJson, ProcessingTtl, http.RequestAborted).ConfigureAwait(false);
        if (!claimed)
        {
            existing = Parse(await values.StringGetAsync(stateKey, http.RequestAborted).ConfigureAwait(false));
            if (!TryReplayOrReject(context, existing, bodyHash))
                context.Result = new ConflictObjectResult(new { Message = "相同请求正在处理中" });
            return;
        }

        using var renewCts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        var renewTask = RenewAsync(atomic, stateKey, processingJson, renewCts.Token);

        ResourceExecutedContext executed;
        try
        {
            executed = await next().ConfigureAwait(false);
        }
        finally
        {
            await renewCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await renewTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the request finishes.
            }
        }

        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            await atomic.TryStringCompareAndDeleteAsync(
                stateKey, processingJson, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (executed.Result is ObjectResult { Value: not null } result
            && (result.StatusCode ?? StatusCodes.Status200OK) is >= 200 and < 300)
        {
            var statusCode = result.StatusCode ?? StatusCodes.Status200OK;
            var completed = new IdemRecord
            {
                State = IdemState.Completed,
                BodyHash = bodyHash,
                StatusCode = statusCode,
                ResponseJson = JsonSerializer.Serialize(result.Value, AppJsonOptions.Default),
                Owner = processing.Owner,
            };
            var completedJson = JsonSerializer.Serialize(completed, AppJsonOptions.Default);
            _ = await atomic.TryStringCompareAndSetAsync(
                stateKey,
                processingJson,
                completedJson,
                CompletedTtl,
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await atomic.TryStringCompareAndDeleteAsync(
            stateKey, processingJson, CancellationToken.None).ConfigureAwait(false);
    }

    private static bool TryReplayOrReject(
        ResourceExecutingContext context,
        IdemRecord? record,
        string bodyHash)
    {
        if (record is null)
            return false;

        if (!string.Equals(record.BodyHash, bodyHash, StringComparison.Ordinal))
        {
            context.Result = new ConflictObjectResult(
                new { Message = "相同 Idempotency-Key 对应不同请求体" });
            return true;
        }

        if (record.State == IdemState.Completed && record.ResponseJson is not null)
        {
            context.HttpContext.Response.Headers["X-Idempotent-Replay"] = "true";
            context.Result = new ContentResult
            {
                StatusCode = record.StatusCode,
                Content = record.ResponseJson,
                ContentType = "application/json",
            };
            return true;
        }

        context.Result = new ConflictObjectResult(new { Message = "相同请求正在处理中" });
        return true;
    }

    private static async Task<string> HashBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        request.Body.Position = 0;
        try
        {
            var hash = await SHA256.HashDataAsync(request.Body, cancellationToken)
                .ConfigureAwait(false);
            return Convert.ToHexString(hash);
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    private static async Task RenewAsync(
        IAtomicCacheStore atomic,
        string key,
        string processingJson,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RenewInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await atomic.TryStringCompareAndExpireAsync(
                    key, processingJson, ProcessingTtl, cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private static IdemRecord? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            return JsonSerializer.Deserialize<IdemRecord>(value, AppJsonOptions.Default);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool CanHaveBody(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method);

    private enum IdemState : byte
    {
        Processing,
        Completed,
    }

    private sealed class IdemRecord
    {
        public IdemState State { get; init; }
        public required string BodyHash { get; init; }
        public required string Owner { get; init; }
        public int StatusCode { get; init; }
        public string? ResponseJson { get; init; }
    }
}
