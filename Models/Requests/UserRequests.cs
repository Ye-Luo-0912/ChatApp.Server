using System.ComponentModel.DataAnnotations;

namespace ChatApp.Server.Models.Requests;

public class UpdateCurrentUserRequest
{
    public string? PhoneNumber { get; set; }

    [StringLength(256)]
    public string? UserName { get; set; }

    [StringLength(500)]
    public string? Signature { get; set; }

    [StringLength(200)]
    public string? Region { get; set; }

    public DateTime? Birthday { get; set; }

    public bool? Gender { get; set; }

    public bool? AllowBeSearched { get; set; }

    public bool? NotifySecurityEmail { get; set; }
}

public class RequestPhoneChangeRequest
{
    [Required]
    public string NewPhoneNumber { get; set; } = string.Empty;
}

public class ConfirmPhoneChangeRequest
{
    [Required]
    [StringLength(8, MinimumLength = 4)]
    public string Code { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    [Required]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [MinLength(6)]
    public string NewPassword { get; set; } = string.Empty;

    /// <summary>
    /// Optional current-session refresh token. When supplied, the server
    /// atomically rotates this session after the security fence advances.
    /// </summary>
    public string? RefreshToken { get; set; }
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
