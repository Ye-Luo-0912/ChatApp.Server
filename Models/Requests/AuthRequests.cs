using System.ComponentModel.DataAnnotations;

namespace ChatApp.Server.Models.Requests;

public class LoginRequest
{
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 6)]
    public string Password { get; set; } = string.Empty;
}

public class LogoutRequest
{
    [Required]
    [StringLength(512, MinimumLength = 16)]
    public string RefreshToken { get; set; } = string.Empty;
}

public class RegisterRequest
{
    [StringLength(64, MinimumLength = 2)]
    public string? Username { get; set; }

    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 6)]
    public string Password { get; set; } = string.Empty;

    [Required]
    [StringLength(8, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;
}

public class RefreshTokenRequest
{
    [Range(1, long.MaxValue)]
    public long UserId { get; set; }

    [Required]
    [StringLength(512, MinimumLength = 16)]
    public string RefreshToken { get; set; } = string.Empty;
}
