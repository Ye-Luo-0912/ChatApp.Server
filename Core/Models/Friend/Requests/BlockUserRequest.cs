using System.ComponentModel.DataAnnotations;

namespace Core.Models.Friend.Requests;

public class BlockUserRequest
{
    [Range(1, long.MaxValue)]
    public long TargetUserId { get; set; }
}
