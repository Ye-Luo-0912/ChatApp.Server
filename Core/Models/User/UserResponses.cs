using Core.Models.Identity;
namespace Core.Models.User;
public sealed class UserProfileResponse
{
    public long Id { get; init; }
    public string? UserName { get; init; }
    public string? Email { get; init; }
    public bool EmailConfirmed { get; init; }
    public string? PhoneNumber { get; init; }
    public string? AvatarUrl { get; init; }
    public bool Gender { get; init; }
    public string? Signature { get; init; }
    public string? Region { get; init; }
    public DateTime? Birthday { get; init; }
    public UserStatus Status { get; init; }
    public DateTimeOffset CreatedDate { get; init; }
    public DateTimeOffset? LastLoginDate { get; init; }

    public static UserProfileResponse FromUser(ApplicationUser user) => new()
    {
        Id            = user.Id,
        UserName      = user.UserName,
        Email         = user.Email,
        EmailConfirmed= user.EmailConfirmed,
        PhoneNumber   = user.PhoneNumber,
        AvatarUrl     = user.AvatarUrl,
        Gender        = user.Gender,
        Signature     = user.Signature,
        Region        = user.Region,
        Birthday      = user.Birthday,
        Status        = user.Status,
        CreatedDate   = user.CreatedDate,
        LastLoginDate = user.LastLoginDate
    };
}
public sealed class PublicUserResponse
{
    public long Id { get; init; }
    public string? UserName { get; init; }
    public string? AvatarUrl { get; init; }
    public string? Signature { get; init; }
    public UserStatus Status { get; init; }

    public static PublicUserResponse FromUser(ApplicationUser user) => new()
    {
        Id        = user.Id,
        UserName  = user.UserName,
        AvatarUrl = user.AvatarUrl,
        Signature = user.Signature,
        Status    = user.Status
    };
}
