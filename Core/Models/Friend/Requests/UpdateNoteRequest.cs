using System.ComponentModel.DataAnnotations;
using Core.Models.Friend;

namespace Core.Models.Friend.Requests;

public class UpdateNoteRequest
{
    [Required]
    [StringLength(FriendshipInputLimits.FriendNoteMaxLength, MinimumLength = 1)]
    public required string Note { get; set; }
}
