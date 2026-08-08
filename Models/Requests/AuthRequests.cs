using System.ComponentModel.DataAnnotations;

namespace ChatApp.Server.Models.Requests;

public class SendResetPasswordCodeRequest
{
    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;
}

public class ResetPasswordRequest
{
    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(8, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 6)]
    public string NewPassword { get; set; } = string.Empty;
}

public class MfaVerifyRequest
{
    [Required]
    [StringLength(128, MinimumLength = 16)]
    public string MfaToken { get; set; } = string.Empty;

    [Required]
    [StringLength(32, MinimumLength = 4)]
    public string Code { get; set; } = string.Empty;
}

public class MfaCodeRequest
{
    [Required]
    [StringLength(32, MinimumLength = 4)]
    public string Code { get; set; } = string.Empty;
}

public class MfaSetupRequest
{
    [Required]
    [StringLength(128, MinimumLength = 6)]
    public string Password { get; set; } = string.Empty;
}

public class MfaDisableRequest
{
    [Required]
    [StringLength(128, MinimumLength = 6)]
    public string Password { get; set; } = string.Empty;

    [Required]
    [StringLength(32, MinimumLength = 4)]
    public string Code { get; set; } = string.Empty;
}
