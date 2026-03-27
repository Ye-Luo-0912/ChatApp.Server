namespace Core.Models.Friend;

public enum RequestStatus:byte
{
    /// <summary>
    /// 请求处于待处理状态
    /// </summary>
    Pending,
    /// <summary>
    /// 请求已被接受
    /// </summary>
    Accepted,
    /// <summary>
    /// 请求已被拒绝
    /// </summary>
    Declined
}