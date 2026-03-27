namespace Core.Models.Friend;

public enum FriendRequestType:byte
{
    /// <summary>
    /// // 收到的请求
    /// </summary>
    Incoming,  
    /// <summary>
    /// // 发出的请求
    /// </summary>
    Outgoing   
}