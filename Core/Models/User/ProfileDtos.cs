using Core.Models.Identity;
using Core.Models.Security;

namespace Core.Models.User;

/// <summary>用户资料更新载荷（邮箱变更走独立流程）。</summary>
public sealed class UpdateProfileRequest
{
    public string? PhoneNumber { get; set; }
    public string? UserName { get; set; }
    public string? Signature { get; set; }
    public string? Region { get; set; }
    public DateTime? Birthday { get; set; }
    public bool? Gender { get; set; }
    public FriendRequestPolicy? FriendRequestPolicy { get; set; }
    public bool? AllowBeSearched { get; set; }
    public bool? NotifyFriendRequests { get; set; }
    public bool? NotifySecurityEmail { get; set; }
}

public sealed class AvatarPresignRequest
{
    public string ContentType { get; set; } = string.Empty;
    public long ContentLength { get; set; }
}

public sealed class AvatarPresignResponse
{
    public string UploadUrl { get; init; } = string.Empty;
    public string PublicUrl { get; init; } = string.Empty;
    public string ObjectKey { get; init; } = string.Empty;
    public string Ticket { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class ConfirmAvatarRequest
{
    public string ObjectKey { get; set; } = string.Empty;
}

public sealed class TrustDeviceRequest
{
    public string? Label { get; set; }
}

public sealed class MarkNotificationsReadRequest
{
    public List<long> Ids { get; set; } = [];
}

public sealed class DisabledUserDto
{
    public long Id { get; init; }
    public string? UserName { get; init; }
    public string? Email { get; init; }
    public DateTimeOffset? LockoutEnd { get; init; }
}

public sealed class SecurityEventDto
{
    public long Id { get; init; }
    public SecurityEventType EventType { get; init; }
    public string? DeviceId { get; init; }
    public string? ClientIp { get; init; }
    public string? Location { get; init; }
    public string? Detail { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class PublicUserSearchResult
{
    public long Id { get; init; }
    public string? UserName { get; init; }
    public string? AvatarUrl { get; init; }
    public string? Signature { get; init; }
}

public sealed class AssignRoleRequest
{
    public string RoleName { get; set; } = KnownRoles.User;
    public string? Reason { get; set; }
}

public sealed class RemoveRoleRequest
{
    public string? Reason { get; set; }
    /// <summary>管理员撤销自己的 Admin 时必须为 true。</summary>
    public bool ConfirmSelfDemotion { get; set; }
}

public sealed class AdminReasonRequest
{
    public string? Reason { get; set; }
}
