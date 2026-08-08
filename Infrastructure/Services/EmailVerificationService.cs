using System.Security.Cryptography;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Models;
using Core.Models.Email;
using Infrastructure.Caching;
using System.Text.Json;

namespace Infrastructure.Services;

/// <summary>
/// 负责邮箱验证码的发送与校验；冷却锁与验证码消费均使用 Redis 原子操作。
/// </summary>
public class EmailVerificationService(
    IEmailSender emailSender,
    ICacheValueStore cache,
    IAtomicCacheStore atomicCache,
    IOneTimeStateStore? oneTimeState = null)
    : IEmailVerificationService
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(15);
    private const int MaxVerifyFailures = 5;
    private readonly IOneTimeStateStore? _oneTimeState = oneTimeState;

    /// <summary>
    /// PR3: 邮箱验证码校验单 Lua 脚本——将 4 次往返合并为 1 次。
    /// KEYS[1] = 验证码键, KEYS[2] = 失败计数键
    /// ARGV[1] = 期望验证码, ARGV[2] = 最大失败次数, ARGV[3] = 失败窗口 TTL(ms)
    /// 返回: {状态码, 当前失败次数}
    ///   1 = Consumed（验证码正确，已消费）
    ///   2 = WrongCodeAndIncremented（验证码错误，失败计数已递增）
    ///   3 = Locked（失败次数已达上限）
    ///   4 = Expired（验证码不存在/已过期）
    /// </summary>
    private const string VerifyEmailCodeScript = """
        local codeKey = KEYS[1]
        local failKey = KEYS[2]
        local expectedCode = ARGV[1]
        local maxFailures = tonumber(ARGV[2])
        local failTtlMs = tonumber(ARGV[3])

        -- 检查失败锁定
        local failCount = tonumber(redis.call('GET', failKey) or '0')
        if failCount >= maxFailures then
            return {3, failCount}
        end

        -- 尝试 CAS-DELETE 验证码
        local current = redis.call('GET', codeKey)
        if current == false then
            -- 验证码不存在/已过期
            return {4, failCount}
        end

        if current == expectedCode then
            -- 验证码正确：删除验证码 + 删除失败计数
            redis.call('DEL', codeKey)
            redis.call('DEL', failKey)
            return {1, 0}
        end

        -- 验证码错误：递增失败计数
        local newFailCount = redis.call('INCR', failKey)
        if newFailCount == 1 then
            redis.call('PEXPIRE', failKey, failTtlMs)
        end
        return {2, newFailCount}
        """;

    /// <inheritdoc />
    public async Task<EmailResult> SendEmailCodeAsync(
        string email, EmailCodePurpose codePurpose, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(email))
            return new EmailResult { IsSuccess = false, ErrorMessage = "邮箱不能为空" };

        try
        {
            var normalizedEmail = NormalizeEmail(email);
            var dataKey = GetCacheKey(normalizedEmail, codePurpose);
            var cooldownKey = GetCooldownKey(normalizedEmail, codePurpose);

            // SET NX：同一邮箱+用途的冷却窗口原子抢占，杜绝并发重复发信。
            var acquiredCooldown = await atomicCache
                .StringSetIfNotExistsAsync(cooldownKey, "1", ResendCooldown, cancellation)
                .ConfigureAwait(false);

            if (!acquiredCooldown)
                return new EmailResult { IsSuccess = false, ErrorMessage = "操作太频繁，请稍后再试" };

            var cachedCode = _oneTimeState is null
                ? await cache.StringGetAsync(dataKey, cancellationToken: cancellation)
                    .ConfigureAwait(false)
                : await _oneTimeState.PeekAsync(dataKey, cancellation).ConfigureAwait(false);
            cachedCode = Unquote(cachedCode);

            string codeToSend;
            var createdNewCode = false;

            if (!string.IsNullOrEmpty(cachedCode))
            {
                codeToSend = cachedCode;
            }
            else
            {
                codeToSend = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
                if (_oneTimeState is not null)
                {
                    await _oneTimeState.IssueAsync(
                            dataKey,
                            codeToSend,
                            DateTimeOffset.UtcNow.Add(CodeLifetime),
                            cancellation)
                        .ConfigureAwait(false);
                    await cache.StringSetAsync(
                            GetExpiryKey(normalizedEmail, codePurpose),
                            DateTimeOffset.UtcNow.Add(CodeLifetime).ToUnixTimeMilliseconds().ToString(),
                            CodeLifetime,
                            cancellation)
                        .ConfigureAwait(false);
                }
                else
                {
                    await cache.StringSetAsync(dataKey, codeToSend, CodeLifetime, cancellation)
                        .ConfigureAwait(false);
                }
                createdNewCode = true;
            }

            var sendResult = await SendVerificationEmailAsync(normalizedEmail, codeToSend, codePurpose, cancellation)
                .ConfigureAwait(false);

            if (!sendResult.IsSuccess)
            {
                // 发送失败时释放冷却，允许立即重试；新生成的码也一并清掉，避免占坑。
                await cache.RemoveAsync(cooldownKey, cancellation).ConfigureAwait(false);
                if (createdNewCode)
                    await cache.RemoveAsync(dataKey, cancellation).ConfigureAwait(false);
            }

            return sendResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new EmailResult
            {
                IsSuccess = false,
                ErrorMessage = $"验证码发送失败: {ex.Message}"
            };
        }
    }

    /// <inheritdoc />
    public async Task<EmailResult> VerifyEmailCodeAsync(
        string email, string code, EmailCodePurpose codePurpose, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
            return new EmailResult { IsSuccess = false, ErrorMessage = "验证码或者邮箱不能为空" };

        var normalizedEmail = NormalizeEmail(email);
        var normalizedCode = code.Trim();
        var cacheKey = GetCacheKey(normalizedEmail, codePurpose);
        var failKey = GetFailKey(normalizedEmail, codePurpose);

        if (_oneTimeState is not null)
        {
            var (claimedResult, claim) = await ClaimEmailCodeAsync(
                    email, normalizedCode, codePurpose, cancellation)
                .ConfigureAwait(false);
            if (claimedResult.IsSuccess && claim is not null)
            {
                try
                {
                    await CompleteEmailCodeAsync(claim, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // The claim remains durably present until its original
                    // expiry; completion cleanup can be retried safely.
                }

                return claimedResult;
            }

            var failures = await atomicCache.StringIncrementAsync(
                    failKey, FailWindow, cancellation)
                .ConfigureAwait(false);
            return failures >= MaxVerifyFailures
                ? new EmailResult { IsSuccess = false, ErrorMessage = "验证失败次数过多，请稍后再试" }
                : claimedResult;
        }

        // PR3: 单次 EVAL 合并「锁定检查 + CAS-DELETE + 区分过期 + 失败递增」4 步原子操作。
        var result = await atomicCache
            .EvaluateScriptAsync(
                VerifyEmailCodeScript,
                [cacheKey, failKey],
                [
                    normalizedCode,
                    MaxVerifyFailures.ToString(),
                    ((long)FailWindow.TotalMilliseconds).ToString()
                ],
                cancellation)
            .ConfigureAwait(false);

        // result[0] 状态码：1=已消费 2=验证码错误且已递增 3=已锁定 4=已过期
        var status = result.Length > 0 ? result[0] : 4L;
        return status switch
        {
            1L => new EmailResult { IsSuccess = true },
            2L => new EmailResult { IsSuccess = false, ErrorMessage = "验证码错误" },
            3L => new EmailResult { IsSuccess = false, ErrorMessage = "验证失败次数过多，请稍后再试" },
            _ => new EmailResult { IsSuccess = false, ErrorMessage = "验证码已过期或尚未发送" }
        };
    }

    public async Task<(EmailResult Result, EmailVerificationClaim? Claim)> ClaimEmailCodeAsync(
        string email,
        string code,
        EmailCodePurpose codePurpose,
        CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
            return (new EmailResult { IsSuccess = false, ErrorMessage = "验证码或者邮箱不能为空" }, null);

        var normalizedEmail = NormalizeEmail(email);
        var normalizedCode = code.Trim();
        var key = GetCacheKey(normalizedEmail, codePurpose);
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(CodeLifetime);

        if (_oneTimeState is not null)
        {
            var peeked = Unquote(await _oneTimeState.PeekAsync(key, cancellation).ConfigureAwait(false));
            if (!string.Equals(peeked, normalizedCode, StringComparison.Ordinal))
                return (new EmailResult { IsSuccess = false, ErrorMessage = "验证码无效或已过期" }, null);

            var expiryRaw = await cache.StringGetAsync(
                    GetExpiryKey(normalizedEmail, codePurpose), cancellation)
                .ConfigureAwait(false);
            if (long.TryParse(expiryRaw, out var expiryMs))
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiryMs);
            if (expiresAt <= now)
                return (new EmailResult { IsSuccess = false, ErrorMessage = "验证码已过期" }, null);

            var claimKey = GetClaimKey(normalizedEmail, codePurpose);
            var claimed = await _oneTimeState.TryClaimAsync<string>(
                    key, claimKey, expiresAt, cancellation)
                .ConfigureAwait(false);
            var consumed = Unquote(claimed?.Payload);
            if (claimed is null || !string.Equals(consumed, normalizedCode, StringComparison.Ordinal))
            {
                if (claimed is not null)
                    await _oneTimeState.RestoreClaimAsync(
                            key, claimKey, expiresAt, CancellationToken.None)
                        .ConfigureAwait(false);
                return (new EmailResult { IsSuccess = false, ErrorMessage = "验证码无效或已过期" }, null);
            }

            return (
                new EmailResult { IsSuccess = true },
                new EmailVerificationClaim(email.Trim(), codePurpose, normalizedCode, expiresAt));
        }

        // Compatibility path for focused tests/legacy hosts. It still uses a
        // compare-and-delete claim, and callers restore on business failure.
        var current = await cache.StringGetAsync(key, cancellation).ConfigureAwait(false);
        if (!string.Equals(current, normalizedCode, StringComparison.Ordinal))
            return (new EmailResult { IsSuccess = false, ErrorMessage = "验证码无效或已过期" }, null);
        if (!await atomicCache.TryStringCompareAndDeleteAsync(key, current!, cancellation)
                .ConfigureAwait(false))
            return (new EmailResult { IsSuccess = false, ErrorMessage = "验证码无效或已过期" }, null);
        return (
            new EmailResult { IsSuccess = true },
            new EmailVerificationClaim(email.Trim(), codePurpose, normalizedCode, expiresAt));
    }

    public async Task CompleteEmailCodeAsync(
        EmailVerificationClaim claim,
        CancellationToken cancellation)
    {
        var normalizedEmail = NormalizeEmail(claim.Email);
        if (_oneTimeState is not null)
        {
            await _oneTimeState.CompleteClaimAsync(
                    GetClaimKey(normalizedEmail, claim.Purpose), cancellation)
                .ConfigureAwait(false);
        }

        await cache.RemoveAsync(
                GetExpiryKey(normalizedEmail, claim.Purpose),
                cancellation)
            .ConfigureAwait(false);
    }

    public async Task RestoreEmailCodeAsync(
        EmailVerificationClaim claim,
        CancellationToken cancellation)
    {
        var remaining = claim.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return;

        var normalizedEmail = NormalizeEmail(claim.Email);
        var key = GetCacheKey(normalizedEmail, claim.Purpose);
        if (_oneTimeState is not null)
        {
            await _oneTimeState.RestoreClaimAsync(
                    key,
                    GetClaimKey(normalizedEmail, claim.Purpose),
                    claim.ExpiresAt,
                    cancellation)
                .ConfigureAwait(false);
        }
        else
        {
            await cache.StringSetAsync(key, claim.Code, remaining, cancellation)
                .ConfigureAwait(false);
        }

        await cache.StringSetAsync(
                GetExpiryKey(normalizedEmail, claim.Purpose),
                claim.ExpiresAt.ToUnixTimeMilliseconds().ToString(),
                remaining,
                cancellation)
            .ConfigureAwait(false);
    }

    private static string GetCacheKey(string normalizedEmail, EmailCodePurpose purpose)
        => $"EmailCode:{purpose}:{normalizedEmail}";

    private static string GetCooldownKey(string normalizedEmail, EmailCodePurpose purpose)
        => $"EmailCooldown:{purpose}:{normalizedEmail}";

    private static string GetFailKey(string normalizedEmail, EmailCodePurpose purpose)
        => $"EmailCodeFail:{purpose}:{normalizedEmail}";

    private static string GetExpiryKey(string normalizedEmail, EmailCodePurpose purpose)
        => $"EmailCodeExpiry:{purpose}:{normalizedEmail}";

    private static string GetClaimKey(string normalizedEmail, EmailCodePurpose purpose)
        => $"EmailCodeClaim:{purpose}:{normalizedEmail}";

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string? Unquote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(value); }
            catch (JsonException) { }
        }
        return value;
    }

    private async Task<EmailResult> SendVerificationEmailAsync(
        string email, string code, EmailCodePurpose codePurpose, CancellationToken cancellation)
    {
        var purposeText = GetPurposeText(codePurpose);
        var subject = $"【ChatApp】{purposeText}验证码";
        var body =
            "<div style='padding: 20px; font-family: sans-serif;'>" +
            $"<h2>您正在进行{purposeText}操作</h2>" +
            $"<p>您的验证码是：<strong style='font-size: 24px; color: #3B82F6;'>{code}</strong></p>" +
            "<p style='color: #666; font-size: 12px;'>验证码 5 分钟内有效，请勿泄露给他人。</p>" +
            "</div>";

        return await emailSender
            .EnqueueEmailAsync(
                email,
                subject,
                body,
                isHtml: true,
                emailType: $"otp:{codePurpose}",
                idempotencyKey: $"otp:{codePurpose}:{email}:{code}",
                cancellation: cancellation)
            .ConfigureAwait(false);
    }

    private static string GetPurposeText(EmailCodePurpose purpose) => purpose switch
    {
        EmailCodePurpose.Register => "注册账号",
        EmailCodePurpose.ResetPassword => "重置密码",
        EmailCodePurpose.BindEmail => "绑定邮箱",
        EmailCodePurpose.ChangeEmail => "更换邮箱",
        _ => "身份验证"
    };
}
