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

    Task<bool> TryConsumeRecoveryCodeAsync(long userId, string code, CancellationToken cancellationToken = default);

    /// <summary>重新生成恢复码（需密码 + 当前 TOTP/恢复码）。</summary>
    Task<(AuthOperationResult Result, string[]? Codes)> RegenerateRecoveryCodesAsync(
        long userId, string password, string codeOrRecovery, CancellationToken cancellationToken = default);
}

public interface ISecurityNotificationService
{
    /// <summary>仅挂起到当前 DbContext，不 SaveChanges（供登录等热路径合并提交）。</summary>
    void StageNotify(long userId, string type, string title, string body, bool preferEmail);

    Task NotifyAsync(
        long userId,
        string type,
        string title,
        string body,
        bool preferEmail,
        CancellationToken cancellationToken = default);
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
