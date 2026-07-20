using System.ComponentModel.DataAnnotations;
using Core.Models.Identity;

namespace ChatApp.Server.Models.Requests;

public class UpdateCurrentUserRequest
{
    [Phone]
    public string? PhoneNumber { get; set; }

    [StringLength(32, MinimumLength = 3)]
    public string? UserName { get; set; }

    [StringLength(500)]
    public string? Signature { get; set; }

    [StringLength(200)]
    public string? Region { get; set; }

    public DateTime? Birthday { get; set; }

    public bool? Gender { get; set; }

    public FriendRequestPolicy? FriendRequestPolicy { get; set; }

    public bool? AllowBeSearched { get; set; }

    public bool? NotifyFriendRequests { get; set; }

    public bool? NotifySecurityEmail { get; set; }
}

public class ChangePasswordRequest
{
    [Required]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [MinLength(6)]
    public string NewPassword { get; set; } = string.Empty;
}

public class RequestEmailChangeRequest
{
    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string NewEmail { get; set; } = string.Empty;
}

public class ConfirmEmailChangeRequest
{
    [Required]
    [StringLength(8, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;
}
