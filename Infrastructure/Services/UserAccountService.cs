using System.Text.RegularExpressions;
using Core.Exceptions;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models;
using Core.Models.Auth;
using Core.Models.Common;
using Core.Models.Identity;
using Core.Models.Security;
using Core.Models.Token;
using Core.Models.User;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// 处理用户资料查询、更新、删除和密码修改。
/// </summary>
public partial class UserAccountService(
    IUserRepository userRepository,
    IPasswordHasher passwordHasher,
    IEmailVerificationService emailVerificationService,
    ISessionStore sessionStore,
    IDeviceInfo deviceInfo,
    IAvatarStorage avatarStorage,
    ISecurityEventStore securityEventStore,
    ISecurityNotificationService securityNotifications,
    ITrustedDeviceService trustedDevices,
    IOptions<ProfileOptions> profileOptions,
    ILogger<UserAccountService> logger) : IUserAccountService
{
    private readonly ProfileOptions _profile = profileOptions.Value;

    public async Task<UserProfileResponse?> GetByIdAsync(long userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await userRepository.FindByIdAsync(userId, cancellationToken);
            return user is null ? null : UserProfileResponse.FromUser(user);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "查找用户 ID {UserId} 时发生异常", userId);
            throw new IdentityException("用户查询失败", ex);
        }
    }

    public async Task<PublicUserResponse?> GetByUserNameAsync(string username, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await userRepository.FindByNameAsync(username, cancellationToken);
            return user is null ? null : PublicUserResponse.FromUser(user);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "查找用户 {Username} 时发生异常", username);
            throw new IdentityException("用户查询失败", ex);
        }
    }

    public Task<CursorPage<PublicUserSearchResult>> SearchUsersAsync(
        string searchTerm, string? cursor = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(searchTerm) || searchTerm.Trim().Length < 2)
        {
            return Task.FromResult(new CursorPage<PublicUserSearchResult>
            {
                Items = [],
                HasMore = false,
                NextCursor = null,
            });
        }

        return userRepository.SearchUsersAsync(searchTerm.Trim(), cursor, limit, cancellationToken);
    }

    public async Task<AuthOperationResult?> UpdateAsync(
        long userId, UpdateProfileRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await userRepository.FindByIdAsync(userId, cancellationToken);
            if (user is null)
                return null;

            if (request.PhoneNumber is not null)
                user.PhoneNumber = request.PhoneNumber;

            if (request.Signature is not null)
                user.Signature = request.Signature.Length <= 500 ? request.Signature : request.Signature[..500];

            if (request.Region is not null)
                user.Region = request.Region.Length <= 200 ? request.Region : request.Region[..200];

            if (request.Birthday.HasValue)
                user.Birthday = request.Birthday;

            if (request.Gender.HasValue)
                user.Gender = request.Gender.Value;

            if (request.FriendRequestPolicy.HasValue)
                user.FriendRequestPolicy = request.FriendRequestPolicy.Value;

            if (request.AllowBeSearched.HasValue)
                user.AllowBeSearched = request.AllowBeSearched.Value;

            if (request.NotifyFriendRequests.HasValue)
                user.NotifyFriendRequests = request.NotifyFriendRequests.Value;

            if (request.NotifySecurityEmail.HasValue)
                user.NotifySecurityEmail = request.NotifySecurityEmail.Value;

            if (!string.IsNullOrWhiteSpace(request.UserName))
            {
                var nameResult = await TryChangeUserNameAsync(user, request.UserName.Trim(), cancellationToken);
                if (!nameResult.Succeeded)
                    return nameResult;
            }

            var ok = await userRepository.UpdateAsync(user, cancellationToken);
            if (ok) logger.LogInformation("成功更新用户 {UserId}", userId);

            return ok
                ? AuthOperationResult.Success()
                : AuthOperationResult.Fail("UpdateFailed", "用户信息更新失败");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "更新用户 {UserId} 时发生异常", userId);
            throw new IdentityException("用户更新失败", ex);
        }
    }

    public async Task<AvatarPresignResponse?> CreateAvatarUploadTicketAsync(
        long userId, string contentType, long contentLength, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null) return null;

        try
        {
            var (objectKey, ticket, uploadUrl, publicUrl, expiresAt) =
                await avatarStorage.CreateUploadTicketAsync(userId, contentType, contentLength, cancellationToken);
            return new AvatarPresignResponse
            {
                ObjectKey = objectKey,
                Ticket = ticket,
                UploadUrl = uploadUrl,
                PublicUrl = publicUrl,
                ExpiresAt = expiresAt,
            };
        }
        catch (ArgumentException)
        {
            throw;
        }
    }

    public async Task<AuthOperationResult?> ConfirmAvatarAsync(
        long userId, string objectKey, string? ticket = null, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null) return null;

        if (string.IsNullOrWhiteSpace(objectKey))
            return AuthOperationResult.Fail("InvalidObjectKey", "无效的头像对象键");

        var oldUrl = user.AvatarUrl;
        var (ok, publicUrl, error) = await avatarStorage.ConfirmObjectAsync(
            userId, objectKey, ticket, cancellationToken);
        if (!ok)
            return AuthOperationResult.Fail("ConfirmFailed", error ?? "头像确认失败");

        user.AvatarUrl = publicUrl;
        var saved = await userRepository.UpdateAsync(user, cancellationToken);
        if (!saved)
            return AuthOperationResult.Fail("UpdateFailed", "头像确认失败");

        _ = avatarStorage.TryDeleteAsync(oldUrl, CancellationToken.None);
        return AuthOperationResult.Success();
    }

    public async Task<AuthOperationResult?> UploadAvatarBytesAsync(
        long userId, string ticket, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null) return null;

        var oldUrl = user.AvatarUrl;
        var (ok, publicUrl, error) = await avatarStorage.StoreAsync(userId, ticket, content, contentType, cancellationToken);
        if (!ok)
            return AuthOperationResult.Fail("UploadFailed", error ?? "头像上传失败");

        user.AvatarUrl = publicUrl;
        var saved = await userRepository.UpdateAsync(user, cancellationToken);
        if (!saved)
            return AuthOperationResult.Fail("UpdateFailed", "头像保存失败");

        _ = avatarStorage.TryDeleteAsync(oldUrl, CancellationToken.None);
        return AuthOperationResult.Success();
    }

    public async Task<AuthOperationResult?> RequestEmailChangeAsync(long userId, string newEmail, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newEmail))
            return AuthOperationResult.Fail("InvalidEmail", "新邮箱不能为空");

        try
        {
            var user = await userRepository.FindByIdAsync(userId, cancellationToken);
            if (user is null)
                return null;

            var trimmed = newEmail.Trim();
            var normalized = trimmed.ToUpperInvariant();

            if (string.Equals(user.NormalizedEmail, normalized, StringComparison.Ordinal))
                return AuthOperationResult.Fail("SameEmail", "新邮箱与当前邮箱相同");

            if (await userRepository.IsEmailTakenAsync(normalized, userId, cancellationToken))
                return AuthOperationResult.Fail("EmailTaken", "该邮箱已被其他账户使用");

            user.PendingEmail = trimmed;
            user.NormalizedPendingEmail = normalized;
            user.PendingEmailRequestedAt = DateTimeOffset.UtcNow;

            var ok = await userRepository.UpdateAsync(user, cancellationToken);
            if (!ok)
                return AuthOperationResult.Fail("UpdateFailed", "无法保存待验证邮箱");

            var send = await emailVerificationService.SendEmailCodeAsync(
                trimmed, EmailCodePurpose.ChangeEmail, cancellationToken);
            if (!send.IsSuccess)
                return AuthOperationResult.Fail("SendCodeFailed", send.ErrorMessage ?? "验证码发送失败");

            logger.LogInformation("用户 {UserId} 发起邮箱变更，目标已写入 PendingEmail", userId);
            return AuthOperationResult.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "用户 {UserId} 发起邮箱变更失败", userId);
            throw new IdentityException("邮箱变更请求失败", ex);
        }
    }

    public async Task<AuthOperationResult?> ConfirmEmailChangeAsync(long userId, string code, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await userRepository.FindByIdAsync(userId, cancellationToken);
            if (user is null)
                return null;

            if (string.IsNullOrWhiteSpace(user.PendingEmail) || string.IsNullOrWhiteSpace(user.NormalizedPendingEmail))
                return AuthOperationResult.Fail("NoPendingEmail", "没有待确认的邮箱变更");

            var pending = user.PendingEmail;
            var normalizedPending = user.NormalizedPendingEmail;

            if (await userRepository.IsEmailTakenAsync(normalizedPending, userId, cancellationToken))
                return AuthOperationResult.Fail("EmailTaken", "该邮箱已被其他账户使用");

            var verify = await emailVerificationService.VerifyEmailCodeAsync(
                pending, code, EmailCodePurpose.ChangeEmail, cancellationToken);
            if (!verify.IsSuccess)
                return AuthOperationResult.Fail("InvalidCode", verify.ErrorMessage ?? "验证码无效");

            user.Email = pending;
            user.NormalizedEmail = normalizedPending;
            user.EmailConfirmed = true;
            user.PendingEmail = null;
            user.NormalizedPendingEmail = null;
            user.PendingEmailRequestedAt = null;
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.AdvanceSecurityVersion();

            var ok = await userRepository.UpdateAsync(user, cancellationToken);
            if (!ok)
                return AuthOperationResult.Fail("UpdateFailed", "邮箱更新失败");

            var currentDevice = deviceInfo.GetDeviceId();
            await sessionStore.RevokeAllSessionsAsync(userId.ToString(), currentDevice, cancellationToken);
            await securityEventStore.RecordAsync(
                userId, SecurityEventType.EmailChanged, currentDevice, deviceInfo.GenerateDeviceInfo().IpAddress,
                detail: "邮箱已变更", cancellationToken: cancellationToken);

            logger.LogInformation("用户 {UserId} 邮箱已确认变更，已撤销其他设备会话", userId);
            return AuthOperationResult.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "用户 {UserId} 确认邮箱变更失败", userId);
            throw new IdentityException("确认邮箱变更失败", ex);
        }
    }

    public async Task<AuthOperationResult?> CancelEmailChangeAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null)
            return null;

        user.PendingEmail = null;
        user.NormalizedPendingEmail = null;
        user.PendingEmailRequestedAt = null;

        var ok = await userRepository.UpdateAsync(user, cancellationToken);
        return ok
            ? AuthOperationResult.Success()
            : AuthOperationResult.Fail("UpdateFailed", "取消邮箱变更失败");
    }

    public async Task<AuthOperationResult?> DeleteAsync(long userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await userRepository.FindByIdAsync(userId, cancellationToken);
            if (user is null)
                return null;

            await sessionStore.RevokeAllSessionsAsync(userId.ToString(), cancellationToken: cancellationToken);

            var ok = await userRepository.DeleteAsync(user, cancellationToken);
            if (ok) logger.LogInformation("成功删除用户 {UserId}", userId);

            return ok
                ? AuthOperationResult.Success()
                : AuthOperationResult.Fail("DeleteFailed", "用户删除失败");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "删除用户 {UserId} 时发生异常", userId);
            throw new IdentityException("用户删除失败", ex);
        }
    }

    public async Task<AuthOperationResult?> ChangePasswordAsync(long userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await userRepository.FindByIdAsync(userId, cancellationToken);
            if (user is null)
                return null;

            if (string.IsNullOrEmpty(user.PasswordHash)
                || !await passwordHasher.VerifyPasswordAsync(currentPassword, user.PasswordHash, cancellationToken))
                return AuthOperationResult.Fail("PasswordMismatch", "当前密码不正确");

            user.PasswordHash = await passwordHasher.HashPasswordAsync(newPassword, cancellationToken);
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.AdvanceSecurityVersion();
            user.AccessFailedCount = 0;
            user.MustChangePassword = false;

            var ok = await userRepository.UpdateAsync(user, cancellationToken);
            if (!ok)
                return AuthOperationResult.Fail("UpdateFailed", "密码修改失败");

            var currentDevice = deviceInfo.GetDeviceId();
            await sessionStore.RevokeAllSessionsAsync(userId.ToString(), currentDevice, cancellationToken);
            await trustedDevices.RevokeAllAsync(userId, cancellationToken);
            await securityEventStore.RecordAsync(
                userId, SecurityEventType.PasswordChanged, currentDevice, deviceInfo.GenerateDeviceInfo().IpAddress,
                cancellationToken: cancellationToken);

            await securityNotifications.NotifyAsync(
                userId, "PasswordChanged", "密码已修改",
                "您的账号密码已修改，其他设备已下线，全部可信设备已失效。", preferEmail: true, cancellationToken);

            logger.LogInformation("用户 {UserId} 密码修改成功，已撤销其他设备会话与可信设备", userId);
            return AuthOperationResult.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (PasswordVerifyOverloadedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "修改用户 {UserId} 密码时发生异常", userId);
            throw new IdentityException("密码修改失败", ex);
        }
    }

    public async Task<AuthOperationResult?> DisableAsync(
        long userId, string? reason, long? actorUserId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null)
            return null;

        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.AdvanceSecurityVersion();

        var ok = await userRepository.UpdateAsync(user, cancellationToken);
        if (!ok)
            return AuthOperationResult.Fail("UpdateFailed", "禁用账户失败");

        await sessionStore.RevokeAllSessionsAsync(userId.ToString(), cancellationToken: cancellationToken);
        await WriteAdminAuditAsync(actorUserId, userId, "DisableUser", reason, cancellationToken);
        await securityEventStore.RecordAsync(
            userId, SecurityEventType.AccountDisabled, actorUserId: actorUserId?.ToString(),
            detail: reason, cancellationToken: cancellationToken);

        logger.LogWarning("用户 {UserId} 已被禁用并强制下线", userId);
        return AuthOperationResult.Success();
    }

    public async Task<AuthOperationResult?> EnableAsync(
        long userId, string? reason, long? actorUserId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null)
            return null;

        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.AdvanceSecurityVersion();

        var ok = await userRepository.UpdateAsync(user, cancellationToken);
        if (!ok)
            return AuthOperationResult.Fail("UpdateFailed", "启用账户失败");

        await WriteAdminAuditAsync(actorUserId, userId, "EnableUser", reason, cancellationToken);
        await securityEventStore.RecordAsync(
            userId, SecurityEventType.AccountEnabled, actorUserId: actorUserId?.ToString(),
            detail: reason, cancellationToken: cancellationToken);

        return AuthOperationResult.Success();
    }

    public Task<CursorPage<DisabledUserDto>> ListDisabledUsersAsync(
        string? cursor = null, int limit = 50, CancellationToken cancellationToken = default)
        => userRepository.ListDisabledUsersAsync(cursor, limit, cancellationToken);

    public async Task<AuthOperationResult?> AssignRoleAsync(
        long userId, string roleName, long actorUserId, string? reason, CancellationToken cancellationToken = default)
    {
        if (!KnownRoles.IsAssignable(roleName))
            return AuthOperationResult.Fail("InvalidRole", "角色不在允许列表中");

        var outcome = await userRepository.MutateRoleAsync(
            userId, roleName, assign: true, actorUserId, reason,
            deviceInfo.GenerateDeviceInfo().IpAddress, cancellationToken);

        return await FinalizeRoleMutationAsync(userId, outcome, cancellationToken);
    }

    public async Task<AuthOperationResult?> RemoveRoleAsync(
        long userId, string roleName, long actorUserId, string? reason, bool confirmSelfDemotion = false,
        CancellationToken cancellationToken = default)
    {
        if (!KnownRoles.IsAssignable(roleName))
            return AuthOperationResult.Fail("InvalidRole", "角色不在允许列表中");

        if (actorUserId == userId
            && string.Equals(roleName.Trim(), KnownRoles.Admin, StringComparison.OrdinalIgnoreCase)
            && !confirmSelfDemotion)
        {
            return AuthOperationResult.Fail("ConfirmRequired", "撤销自己的 Admin 角色需要 ConfirmSelfDemotion=true");
        }

        var outcome = await userRepository.MutateRoleAsync(
            userId, roleName, assign: false, actorUserId, reason,
            deviceInfo.GenerateDeviceInfo().IpAddress, cancellationToken);

        return await FinalizeRoleMutationAsync(userId, outcome, cancellationToken);
    }

    private async Task<AuthOperationResult?> FinalizeRoleMutationAsync(
        long userId, RoleMutationOutcome outcome, CancellationToken cancellationToken)
    {
        switch (outcome)
        {
            case RoleMutationOutcome.UserNotFound:
                return null;
            case RoleMutationOutcome.RoleNotFound:
                return AuthOperationResult.Fail("RoleNotFound", "角色不存在");
            case RoleMutationOutcome.LastAdmin:
                return AuthOperationResult.Fail("LastAdmin", "不能撤销最后一个管理员");
            case RoleMutationOutcome.AlreadyHasRole:
            case RoleMutationOutcome.RoleNotAssigned:
            case RoleMutationOutcome.Success:
                break;
            default:
                return AuthOperationResult.Fail("RoleMutationFailed", "角色变更失败");
        }

        if (outcome is RoleMutationOutcome.Success)
        {
            await sessionStore.RevokeAllSessionsAsync(userId.ToString(), cancellationToken: cancellationToken);
            logger.LogWarning("用户 {UserId} 角色已变更，已撤销全部会话", userId);
            await securityNotifications.NotifyAsync(
                userId, "RoleChanged", "角色已变更",
                "您的账号角色已变更，全部会话已下线，请重新登录。",
                preferEmail: true, cancellationToken);
        }

        return AuthOperationResult.Success();
    }

    public async Task<IReadOnlyList<SessionDeviceDto>> ListSessionsAsync(
        long userId, string? currentDeviceId, CancellationToken cancellationToken = default)
    {
        var sessions = await sessionStore.ListSessionsAsync(userId.ToString(), cancellationToken);
        return sessions
            .Select(s => SessionDeviceDto.From(s, currentDeviceId is not null
                && string.Equals(s.DeviceId, currentDeviceId, StringComparison.Ordinal)))
            .OrderByDescending(s => s.LastActiveAt)
            .ToList();
    }

    public async Task RevokeSessionAsync(long userId, string deviceId, CancellationToken cancellationToken = default)
    {
        await sessionStore.RevokeSessionAsync(userId.ToString(), deviceId, cancellationToken);
        await securityEventStore.RecordAsync(
            userId, SecurityEventType.SessionRevoked, deviceId, detail: "用户撤销会话",
            cancellationToken: cancellationToken);
    }

    public Task<int> RevokeOtherSessionsAsync(long userId, string currentDeviceId, CancellationToken cancellationToken = default)
        => sessionStore.RevokeAllSessionsAsync(userId.ToString(), currentDeviceId, cancellationToken);

    public async Task<int> ForceLogoutAsync(
        long userId, string? reason, long? actorUserId, CancellationToken cancellationToken = default)
    {
        var count = await sessionStore.RevokeAllSessionsAsync(userId.ToString(), cancellationToken: cancellationToken);
        await WriteAdminAuditAsync(actorUserId, userId, "ForceLogout", reason, cancellationToken);
        await securityEventStore.RecordAsync(
            userId, SecurityEventType.ForceLogout, actorUserId: actorUserId?.ToString(),
            detail: reason, cancellationToken: cancellationToken);
        return count;
    }

    public Task<CursorPage<SecurityEventDto>> ListSecurityEventsAsync(
        long userId, string? cursor = null, int limit = 50, CancellationToken cancellationToken = default)
        => userRepository.ListSecurityEventsAsync(userId, cursor, limit, cancellationToken);

    public async Task<AuthOperationResult?> ReportNotMeAsync(
        long userId, long securityEventId, CancellationToken cancellationToken = default)
    {
        var evt = await userRepository.GetSecurityEventAsync(userId, securityEventId, cancellationToken);
        if (evt is null)
            return AuthOperationResult.Fail("NotFound", "安全事件不存在");

        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null) return null;

        if (!string.IsNullOrWhiteSpace(evt.DeviceId))
            await sessionStore.RevokeSessionAsync(userId.ToString(), evt.DeviceId, cancellationToken);

        await sessionStore.RevokeAllSessionsAsync(userId.ToString(), cancellationToken: cancellationToken);
        await trustedDevices.RevokeAllAsync(userId, cancellationToken);
        user.MustChangePassword = true;
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.AdvanceSecurityVersion();
        await userRepository.UpdateAsync(user, cancellationToken);

        await securityEventStore.RecordAsync(
            userId, SecurityEventType.NotMeReported, evt.DeviceId, evt.ClientIp,
            detail: $"sourceEvent={securityEventId}", cancellationToken: cancellationToken);

        await securityNotifications.NotifyAsync(
            userId, "NotMeReported", "已标记非本人操作",
            "已撤销相关设备、可信设备，并要求修改密码。请立即通过“忘记密码”或登录后的改密流程更新密码。",
            preferEmail: true, cancellationToken);

        return AuthOperationResult.Success();
    }

    public async Task<AuthOperationResult?> RejectSuspiciousLoginAsync(
        long userId, long securityEventId, CancellationToken cancellationToken = default)
    {
        var evt = await userRepository.GetSecurityEventAsync(userId, securityEventId, cancellationToken);
        if (evt is null)
            return AuthOperationResult.Fail("NotFound", "安全事件不存在");

        if (evt.EventType is not (SecurityEventType.LoginUnusualLocation or SecurityEventType.LoginNewDevice
            or SecurityEventType.LoginSuccess))
            return AuthOperationResult.Fail("InvalidEvent", "仅可拒绝新设备/异常登录类事件");

        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null) return null;

        // 优先撤销该次登录的设备会话；SessionId 是结构化列，不再从 Detail 解析。
        if (!string.IsNullOrWhiteSpace(evt.DeviceId))
            await sessionStore.RevokeSessionAsync(userId.ToString(), evt.DeviceId, cancellationToken);

        // 匹配 DeviceIdHint 的可信设备一并吊销（不核销全部）。
        if (!string.IsNullOrWhiteSpace(evt.DeviceId))
        {
            var devices = await trustedDevices.ListAsync(userId, cancellationToken);
            foreach (var d in devices.Where(d =>
                         string.Equals(d.DeviceIdHint, evt.DeviceId, StringComparison.Ordinal)))
            {
                await trustedDevices.RemoveAsync(userId, d.Id, cancellationToken);
            }
        }

        await securityEventStore.RecordAsync(
            userId, SecurityEventType.LoginRejected, evt.DeviceId, evt.ClientIp,
            detail: $"sourceEvent={securityEventId}",
            cancellationToken: cancellationToken,
            sessionId: evt.SessionId);

        await securityNotifications.NotifyAsync(
            userId, "LoginRejected", "已拒绝可疑登录",
            "已撤销该次登录关联的设备会话。若仍有异常，请使用「非本人」撤销全部会话并修改密码。",
            preferEmail: true, cancellationToken);

        return AuthOperationResult.Success();
    }

    private async Task<AuthOperationResult> TryChangeUserNameAsync(
        ApplicationUser user, string newName, CancellationToken cancellationToken)
    {
        if (newName.Length < _profile.UserNameMinLength || newName.Length > _profile.UserNameMaxLength)
            return AuthOperationResult.Fail("InvalidUserName", "用户名长度不符合要求");

        if (!UserNameRegex().IsMatch(newName))
            return AuthOperationResult.Fail("InvalidUserName", "用户名仅允许字母、数字和下划线");

        if (string.Equals(user.UserName, newName, StringComparison.Ordinal))
            return AuthOperationResult.Success();

        var cooldown = TimeSpan.FromDays(Math.Max(1, _profile.UserNameCooldownDays));
        if (user.UserNameChangedAt is { } last
            && DateTimeOffset.UtcNow - last < cooldown)
        {
            var remain = cooldown - (DateTimeOffset.UtcNow - last);
            return AuthOperationResult.Fail("UserNameCooldown",
                $"用户名冷却中，约 {Math.Ceiling(remain.TotalDays)} 天后可再次修改");
        }

        var normalized = newName.ToUpperInvariant();
        if (await userRepository.IsUserNameTakenAsync(normalized, user.Id, cancellationToken))
            return AuthOperationResult.Fail("UserNameTaken", "用户名已被占用");

        user.UserName = newName;
        user.NormalizedUserName = normalized;
        user.UserNameChangedAt = DateTimeOffset.UtcNow;
        return AuthOperationResult.Success();
    }

    private async Task WriteAdminAuditAsync(
        long? actorUserId, long? targetUserId, string action, string? reason,
        CancellationToken cancellationToken, string? detail = null)
    {
        if (actorUserId is null) return;
        await userRepository.AddAdminAuditAsync(new AdminAuditLog
        {
            AdminUserId = actorUserId.Value,
            TargetUserId = targetUserId,
            Action = action,
            Reason = reason,
            Detail = detail,
            ClientIp = deviceInfo.GenerateDeviceInfo().IpAddress,
            CreatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);
    }

    [GeneratedRegex("^[a-zA-Z0-9_]{3,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserNameRegex();
}
