namespace Core.Models.Auth;

/// <summary>
/// 操作失败时返回的错误描述，替代 IdentityError。
/// </summary>
public sealed record AuthOperationError(string Code, string Description);
