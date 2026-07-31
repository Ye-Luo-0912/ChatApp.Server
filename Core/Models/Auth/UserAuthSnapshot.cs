namespace Core.Models.Auth;

/// <summary>
/// 登录/刷新使用的短期认证快照。SecurityVersion 是缓存的版本栅栏，
/// 角色或账户安全状态变化后，旧快照不会被重新使用。
/// </summary>
public sealed class UserAuthSnapshot
{
    public long UserId { get; set; }
    public long SecurityVersion { get; set; }
    public string[] Roles { get; set; } = [];
}
