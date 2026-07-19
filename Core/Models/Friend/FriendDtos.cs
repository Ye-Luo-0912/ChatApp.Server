using System.ComponentModel.DataAnnotations;

namespace Core.Models.Friend;
public record ApiResponse(object? Data);
public record ApiError(FriendshipOperationResultErrorCode Code, string Message);
public record SendFriendRequestRequest([Required] long TargetUserId, string? Message);
public class FriendDto
{
    public long FriendId { get ; set ; }
    public string? FriendName { get; set; } = string.Empty;
    public string? Note { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    
    public string? AvatarUrl { get; set; }
    
    public int? GroupId { get; set; }
    
    public string? GroupName { get; set; }
    
    public DateTime? LastInteractionAt { get; set; }
    
    public DateTimeOffset? LastSeenAt { get; set; }
}
public class FriendRequestDto
{
    public long RequestId { get; set; }
    public long RequesterId { get; set; }
    public long TargetUserId { get; set; }
    public string? Message { get; set; }
    public RequestStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}
