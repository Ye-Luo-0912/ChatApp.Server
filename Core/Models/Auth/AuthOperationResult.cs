namespace Core.Models.Auth;

/// <summary>
/// 通用操作结果，替代 IdentityResult。
/// </summary>
public sealed class AuthOperationResult
{
    public bool Succeeded { get; private init; }
    public IReadOnlyCollection<AuthOperationError> Errors { get; private init; } = [];

    public static AuthOperationResult Success() => new() { Succeeded = true };

    public static AuthOperationResult Fail(params AuthOperationError[] errors) =>
        new() { Errors = errors };

    public static AuthOperationResult Fail(string code, string description) =>
        Fail(new AuthOperationError(code, description));
}
