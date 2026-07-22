using Core.Models.Auth;
using Core.Models.Common;
using Core.Models.Token;
using Core.Models.User;

namespace Core.Interfaces;

/// <summary>
/// 定义用户账户资料模块的核心能力。
/// </summary>
public interface IUserAccountService
{
    Task<UserProfileResponse?> GetByIdAsync(long userId, CancellationToken cancellationToken = default);

    Task<PublicUserResponse?> GetByUserNameAsync(string username, CancellationToken cancellationToken = default);

    Task<CursorPage<PublicUserSearchResult>> SearchUsersAsync(
        string searchTerm, string? cursor = null, int limit = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新非邮箱资料。邮箱变更请走 <see cref="RequestEmailChangeAsync"/>。
    /// </summary>
    Task<AuthOperationResult?> UpdateAsync(long userId, UpdateProfileRequest request, CancellationToken cancellationToken = default);

    Task<AvatarPresignResponse?> CreateAvatarUploadTicketAsync(
        long userId, string contentType, long contentLength, CancellationToken cancellationToken = default);

    Task<AuthOperationResult?> ConfirmAvatarAsync(
        long userId, string objectKey, string? ticket = null, CancellationToken cancellationToken = default);

    Task<AuthOperationResult?> UploadAvatarBytesAsync(
        long userId, string ticket, Stream content, string contentType, CancellationToken cancellationToken = default);

    Task<AuthOperationResult?> RequestEmailChangeAsync(long userId, string newEmail, CancellationToken cancellationToken = default);

    Task<AuthOperationResult?> ConfirmEmailChangeAsync(long userId, string code, CancellationToken cancellationToken = default);

    Task<AuthOperationResult?> CancelEmailChangeAsync(long userId, CancellationToken cancellationToken = default);

    Task<AuthOperationResult?> DeleteAsync(long userId, CancellationToken cancellationToken = default);

    Task<AuthOperationResult?> ChangePasswordAsync(long userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default);

    Task<AuthOperationResult?> DisableAsync(long userId, string? reason, long? actorUserId, CancellationToken cancellationToken = default);

    Task<AuthOperationResult?> EnableAsync(long userId, string? reason, long? actorUserId, CancellationToken cancellationToken = default);

    Task<CursorPage<DisabledUserDto>> ListDisabledUsersAsync(
        string? cursor = null, int limit = 50, CancellationToken cancellationToken = default);

    Task<AuthOperationResult?> AssignRoleAsync(
        long userId, string roleName, long actorUserId, string? reason, CancellationToken cancellationToken = default);

    Task<AuthOperationResult?> RemoveRoleAsync(
        long userId, string roleName, long actorUserId, string? reason, bool confirmSelfDemotion = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionDeviceDto>> ListSessionsAsync(long userId, string? currentDeviceId, CancellationToken cancellationToken = default);

    Task RevokeSessionAsync(long userId, string deviceId, CancellationToken cancellationToken = default);

    Task<int> RevokeOtherSessionsAsync(long userId, string currentDeviceId, CancellationToken cancellationToken = default);

    Task<int> ForceLogoutAsync(long userId, string? reason, long? actorUserId, CancellationToken cancellationToken = default);

    Task<CursorPage<SecurityEventDto>> ListSecurityEventsAsync(
        long userId, string? cursor = null, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>标记安全事件“不是本人”：撤销对应设备、全部会话，并要求修改密码。</summary>
    Task<AuthOperationResult?> ReportNotMeAsync(long userId, long securityEventId, CancellationToken cancellationToken = default);
}
