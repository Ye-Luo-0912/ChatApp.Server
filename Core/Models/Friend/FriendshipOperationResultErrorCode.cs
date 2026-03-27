namespace Core.Models.Friend;

/// <summary>
/// 该枚举用于定义好友操作结果的错误码，涵盖了在处理好友关系操作时可能出现的各种错误情况。
/// </summary>
public enum FriendshipOperationResultErrorCode:byte
{
    /// <summary>
    /// 未定义的错误，通常用于表示在当前上下文中没有具体的错误代码被指定。
    /// </summary>
    None = 0,
    
    /// <summary>
    /// 操作成功，无错误发生。
    /// </summary>
    Success = 1,

    /// <summary>
    /// 表示在处理好友操作时，由于验证失败导致的操作无法完成。这可能是因为请求的数据不符合系统的要求或违反了业务规则。
    /// </summary>
    ValidationFailed = 2,

    /// <summary>
    /// 好友请求已存在，无需重复发送。
    /// </summary>
    FriendshipRequestAlreadyExists = 3,

    /// <summary>
    /// 好友关系已存在，不能再次添加为好友。
    /// </summary>
    FriendshipAlreadyExists = 4,

    /// <summary>
    /// 未找到对应的好友请求，可能是请求已过期或已被处理。
    /// </summary>
    FriendshipRequestNotFound = 5,

    /// <summary>
    /// 未找到对应的好友关系，无法进行相关操作。
    /// </summary>
    FriendshipNotFound = 6,

    /// <summary>
    /// 权限不足，当前用户没有执行该操作的权限。
    /// </summary>
    InsufficientPermissions = 7,

    /// <summary>
    /// 系统内部错误，可能是数据库连接失败、服务器故障等原因导致。
    /// </summary>
    InternalSystemError = 8,

    /// <summary>
    /// 好友请求已过期，无法再进行处理。
    /// </summary>
    FriendshipRequestExpired = 9,

    /// <summary>
    /// 发起的好友请求已被对方阻止，可能是请求方已被接收方拉黑。
    /// </summary>
    RequestAlreadyBlocked = 10,

    /// <summary>
    /// 好友分组未定义
    /// </summary>
    FriendGroupNotFound = 11,
}