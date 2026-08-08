using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Interfaces.Cache;
using Core.Settings;
using Infrastructure.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        var stateKey = BuildStateKey(http, userId, idempotencyKey);
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
        Exception? renewFailure = null;

        ResourceExecutedContext? executed = null;
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
            catch (Exception ex)
            {
                // A renewal failure makes the cache lease uncertain; it must
                // never turn a committed controller action into HTTP 500.
                renewFailure = ex;
                http.RequestServices
                    .GetRequiredService<ILogger<IdempotentAttribute>>()
                    .LogWarning(ex, "幂等键续租失败，进入 LeaseUncertain 状态 Key={Key}", stateKey);
            }
        }

        if (executed is null)
            return;

        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            if (renewFailure is null)
            {
                await atomic.TryStringCompareAndDeleteAsync(
                    stateKey, processingJson, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await MarkUncertainAsync(atomic, stateKey, processingJson, bodyHash)
                    .ConfigureAwait(false);
            }
            return;
        }

        if (executed.Result is ObjectResult { Value: not null } result
            && (result.StatusCode ?? StatusCodes.Status200OK) is >= 200 and < 300)
        {
            if (renewFailure is not null)
            {
                // The controller result is authoritative, but the cache lease
                // is not. Never overwrite a committed business result with a
                // 500 merely because the idempotency lease was lost.
                await MarkUncertainAsync(atomic, stateKey, processingJson, bodyHash)
                    .ConfigureAwait(false);
                return;
            }

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
            var finalized = await atomic.TryStringCompareAndSetAsync(
                stateKey,
                processingJson,
                completedJson,
                CompletedTtl,
                CancellationToken.None).ConfigureAwait(false);
            if (!finalized)
            {
                http.RequestServices
                    .GetRequiredService<ILogger<IdempotentAttribute>>()
                    .LogWarning(
                        "幂等完成记录写入失败，业务结果保持成功但状态为 LeaseUncertain Key={Key}",
                        stateKey);
                await MarkUncertainAsync(atomic, stateKey, processingJson, bodyHash)
                    .ConfigureAwait(false);
            }
            return;
        }

        if (renewFailure is null)
        {
            await atomic.TryStringCompareAndDeleteAsync(
                stateKey, processingJson, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await MarkUncertainAsync(atomic, stateKey, processingJson, bodyHash)
                .ConfigureAwait(false);
        }
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

        context.Result = new ConflictObjectResult(new
        {
            Message = record.State == IdemState.LeaseUncertain
                ? "相同请求的业务结果可能已经提交，请查询业务状态后再重试"
                : "相同请求正在处理中",
        });
        return true;
    }

    private static async Task MarkUncertainAsync(
        IAtomicCacheStore atomic,
        string stateKey,
        string processingJson,
        string bodyHash)
    {
        var uncertain = JsonSerializer.Serialize(
            new IdemRecord
            {
                State = IdemState.LeaseUncertain,
                BodyHash = bodyHash,
                Owner = "uncertain",
            },
            AppJsonOptions.Default);
        try
        {
            await atomic.TryStringCompareAndSetAsync(
                    stateKey,
                    processingJson,
                    uncertain,
                    CompletedTtl,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // The cache failure is already represented by an uncertain lease;
            // the business endpoint result remains authoritative.
        }
    }

    private static string BuildStateKey(
        HttpContext http,
        string userId,
        string rawKey)
    {
        var security = http.RequestServices
            .GetRequiredService<IOptions<SecurityOptions>>()
            .Value;
        var secret = security.SecretEncryptionKey;
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Security:SecretEncryptionKey 必须用于幂等键哈希");

        var secretByteCount = Encoding.UTF8.GetByteCount(secret);
        var keyByteCount = Encoding.UTF8.GetByteCount(rawKey);
        byte[]? rentedSecret = null;
        byte[]? rentedKey = null;
        try
        {
            Span<byte> secretBytes = secretByteCount <= 512
                ? stackalloc byte[secretByteCount]
                : (rentedSecret = ArrayPool<byte>.Shared.Rent(secretByteCount))
                    .AsSpan(0, secretByteCount);
            Span<byte> keyBytes = keyByteCount <= 512
                ? stackalloc byte[keyByteCount]
                : (rentedKey = ArrayPool<byte>.Shared.Rent(keyByteCount))
                    .AsSpan(0, keyByteCount);
            Encoding.UTF8.GetBytes(secret, secretBytes);
            Encoding.UTF8.GetBytes(rawKey, keyBytes);

            Span<byte> digest = stackalloc byte[HMACSHA256.HashSizeInBytes];
            HMACSHA256.HashData(secretBytes, keyBytes, digest);
            var keyHash = Convert.ToHexString(digest);
            return $"idem:{userId}:{http.Request.Method}:{http.Request.Path}:{keyHash}";
        }
        finally
        {
            if (rentedSecret is not null)
                ArrayPool<byte>.Shared.Return(rentedSecret);
            if (rentedKey is not null)
                ArrayPool<byte>.Shared.Return(rentedKey);
        }
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
                throw new IdempotencyLeaseLostException();
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
        LeaseUncertain,
    }

    private sealed class IdempotencyLeaseLostException : Exception
    {
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
