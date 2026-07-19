namespace Core.Models.Auth;

public sealed class UserRegistrationResult
{
    public bool IsSuccess { get; private init; }
    public long? UserId { get; private init; }
    public string? Username { get; private init; }
    public IReadOnlyCollection<AuthOperationError> Errors { get; private init; } = [];
    public string? Message { get; private init; }

    public static UserRegistrationResult Success(long userId, string username) => new()
    {
        IsSuccess = true,
        UserId = userId,
        Username = username
    };

    public static UserRegistrationResult Fail(IEnumerable<AuthOperationError> errors, string? message = null) => new()
    {
        Errors = errors.ToArray(),
        Message = message
    };
}
