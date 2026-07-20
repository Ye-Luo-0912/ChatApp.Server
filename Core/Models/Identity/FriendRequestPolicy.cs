namespace Core.Models.Identity;

/// <summary>谁可以向该用户发送好友申请。</summary>
public enum FriendRequestPolicy : byte
{
    /// <summary>所有人可申请，系统自动通过成为好友。</summary>
    Everyone = 0,
    /// <summary>需要对方验证（进入待处理申请）。</summary>
    RequireVerification = 1,
    /// <summary>禁止陌生人申请。</summary>
    NoStrangers = 2,
}
