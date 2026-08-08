namespace Core.Models.Friend;

public class BlockedUserDto
{
    public long UserId { get; set; }
    public string? UserName { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime BlockedAt { get; set; }
}
