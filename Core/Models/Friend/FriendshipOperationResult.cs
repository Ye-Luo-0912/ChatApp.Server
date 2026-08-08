namespace Core.Models.Friend;

public class FriendshipOperationResult
{
    public bool IsSuccess { get; set; }
    public FriendshipOperationResultErrorCode ErrorCode { get; set; }
    public string? Message { get; set; }

   
    public static FriendshipOperationResult Failed(FriendshipOperationResultErrorCode code, string? msg = null) => new()
    {
        IsSuccess = false, 
        ErrorCode = code,
        Message = msg,
    };

    public static FriendshipOperationResult Success(string? msg = null) => new()
    {
        IsSuccess = true,
        ErrorCode = FriendshipOperationResultErrorCode.None,
        Message = msg,
    };
}
public class FriendshipOperationResult<T>
{
    public bool Succeeded { get; set; }
    public FriendshipOperationResultErrorCode ErrorCode { get; set; }
    public string? Message { get; set; }
    public T? Data { get; set; }

    public static FriendshipOperationResult<T> Failed(FriendshipOperationResultErrorCode code, string? msg = null) => new()
    {
        Succeeded = false,
        ErrorCode = code,
        Message = msg,
        Data = default
    };

    public static FriendshipOperationResult<T> Success(T data, string? msg = null) => new()
    {
        Succeeded = true,
        ErrorCode = FriendshipOperationResultErrorCode.None,
        Message = msg,
        Data = data
    };
}

