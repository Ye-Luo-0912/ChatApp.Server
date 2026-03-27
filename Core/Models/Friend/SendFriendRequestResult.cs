using Core.Models.DTOs;

namespace Core.Models.Friend;

public class SendFriendRequestResult : FriendshipOperationResult
{
    public SendFriendRequestOutcome Outcome { get; set; }
    public FriendDto? Friend { get; set; }

    public static SendFriendRequestResult Success(
        SendFriendRequestOutcome outcome,
        string? msg = null,
        FriendDto? friend = null) => new()
    {
        IsSuccess = true,
        ErrorCode = FriendshipOperationResultErrorCode.None,
        Message = msg,
        Outcome = outcome,
        Friend = friend
    };

    public new static SendFriendRequestResult Failed(
        FriendshipOperationResultErrorCode code,
        string? msg = null) => new()
    {
        IsSuccess = false,
        ErrorCode = code,
        Message = msg,
        Outcome = SendFriendRequestOutcome.None,
        Friend = null
    };
}