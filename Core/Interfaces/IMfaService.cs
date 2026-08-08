using Core.Models.Auth;
using Core.Models.Common;
using Core.Models.Identity;
using Core.Models.Security;
using Core.Models.Token;

namespace Core.Interfaces;

public interface IMfaService
{
    Task<(string SharedKey, string OtpAuthUri, string[] RecoveryCodes)> BeginSetupAsync(
        long userId, string password, CancellationToken cancellationToken = default);

    Task<AuthOperationResult> ConfirmSetupAsync(long userId, string code, CancellationToken cancellationToken = default);

    Task<AuthOperationResult> DisableAsync(
        long userId, string password, string codeOrRecovery, CancellationToken cancellationToken = default);

    bool VerifyTotp(string sharedKey, string code);

    bool VerifyTotpForUser(ApplicationUser user, string code);

    /// <summary>
    /// 校验 TOTP 并原子消费该时间步（Redis SET NX），同一 timestep 不可重放。
    /// </summary>
    Task<bool> TryVerifyAndConsumeTotpForUserAsync(
        ApplicationUser user, string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims a TOTP time-step for a mutation. A caller must restore the claim
    /// when its database mutation rolls back; a successful commit leaves the
    /// replay-prevention marker in place until its TTL expires.
    /// </summary>
    Task<MfaVerificationClaim?> TryClaimTotpForUserAsync(
        ApplicationUser user, string code, CancellationToken cancellationToken = default);

    Task RestoreTotpClaimAsync(
        MfaVerificationClaim claim, CancellationToken cancellationToken = default);

    /// <summary>
    /// 将恢复码从可用列表移动到持久化 Claim。调用方必须完成或恢复该 Claim。
    /// </summary>
    Task<MfaRecoveryCodeClaim?> TryClaimRecoveryCodeForUserAsync(
        long userId, string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// 完成恢复码 Claim。成功时推进账号安全版本，并返回新版本。
    /// </summary>
    Task<long?> CompleteRecoveryCodeClaimAsync(
        MfaRecoveryCodeClaim claim, CancellationToken cancellationToken = default);

    /// <summary>业务失败时恢复恢复码；不会延长其 Claim 截止时间。</summary>
    Task<bool> RestoreRecoveryCodeClaimAsync(
        MfaRecoveryCodeClaim claim, CancellationToken cancellationToken = default);

    Task<bool> TryConsumeRecoveryCodeAsync(long userId, string code, CancellationToken cancellationToken = default);

    /// <summary>重新生成恢复码（需密码 + 当前 TOTP/恢复码）。</summary>
    Task<(AuthOperationResult Result, string[]? Codes)> RegenerateRecoveryCodesAsync(
        long userId, string password, string codeOrRecovery, CancellationToken cancellationToken = default);
}

public interface ISecurityNotificationService
{
    /// <summary>仅挂起到当前 DbContext，不 SaveChanges（供登录等热路径合并提交）。</summary>
    void StageNotify(long userId, string type, string title, string body, bool preferEmail);

    /// <param name="idempotencyKey">可选幂等键；为空时按「用户+类型+分钟」去重。</param>
    Task NotifyAsync(
        long userId,
        string type,
        string title,
        string body,
        bool preferEmail,
        CancellationToken cancellationToken = default,
        string? idempotencyKey = null);
}

public interface IAdminAuditQuery
{
    Task<CursorPage<AdminAuditLogDto>> QueryAsync(
        long? adminUserId,
        long? targetUserId,
        string? action,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);
}

public sealed class AdminAuditLogDto
{
    public long Id { get; init; }
    public long AdminUserId { get; init; }
    public long? TargetUserId { get; init; }
    public string Action { get; init; } = "";
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public string? ClientIp { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
