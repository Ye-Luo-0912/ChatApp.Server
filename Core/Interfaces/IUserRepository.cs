using Core.Models.Common;
using Core.Models.Identity;
using Core.Models.Security;
using Core.Models.User;

namespace Core.Interfaces;

/// <summary>
/// 用户数据访问抽象。
/// </summary>
public interface IUserRepository
{
    Task<ApplicationUser?> FindByIdAsync(long userId, CancellationToken cancellationToken = default);
    Task<ApplicationUser?> FindByNameAsync(string username, CancellationToken cancellationToken = default);
    Task<ApplicationUser?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<bool> IsEmailTakenAsync(string normalizedEmail, long? excludeUserId = null, CancellationToken cancellationToken = default);
    Task<bool> IsUserNameTakenAsync(string normalizedUserName, long? excludeUserId = null, CancellationToken cancellationToken = default);
    Task<bool> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken = default);

    Task<CursorPage<PublicUserSearchResult>> SearchUsersAsync(
        string searchTerm, string? cursor, int limit, CancellationToken cancellationToken = default);

    Task<CursorPage<DisabledUserDto>> ListDisabledUsersAsync(
        string? cursor, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetRoleNamesAsync(long userId, CancellationToken cancellationToken = default);

    Task<int> CountUsersInRoleAsync(string roleName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 角色变更 + 审计 + 安全事件同一事务。不含会话撤销。
    /// </summary>
    Task<RoleMutationOutcome> MutateRoleAsync(
        long userId,
        string roleName,
        bool assign,
        long actorUserId,
        string? reason,
        string? clientIp,
        CancellationToken cancellationToken = default);

    Task AddAdminAuditAsync(AdminAuditLog log, CancellationToken cancellationToken = default);

    Task<CursorPage<SecurityEventDto>> ListSecurityEventsAsync(
        long userId, string? cursor, int limit, CancellationToken cancellationToken = default);

    Task<SecurityEventDto?> GetSecurityEventAsync(
        long userId, long eventId, CancellationToken cancellationToken = default);
}

public enum RoleMutationOutcome
{
    Success = 0,
    UserNotFound = 1,
    RoleNotFound = 2,
    AlreadyHasRole = 3,
    RoleNotAssigned = 4,
    LastAdmin = 5,
}
