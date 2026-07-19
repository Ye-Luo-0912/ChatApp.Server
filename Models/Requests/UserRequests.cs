using System.ComponentModel.DataAnnotations;
namespace ChatApp.Server.Models.Requests;
public class UpdateCurrentUserRequest
{
    [EmailAddress]
    public string? Email { get; set; }
    [Phone]
    public string? PhoneNumber { get; set; }
}
public class ChangePasswordRequest
{
    [Required]
    public string CurrentPassword { get; set; } = string.Empty;
    [Required]
    [MinLength(6)]
    public string NewPassword { get; set; } = string.Empty;
}
