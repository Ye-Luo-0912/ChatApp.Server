using Core.Models.Token;

namespace Core.Models.Auth;

/// <summary>
/// Security mutation response. Tokens are deliberately kept outside the
/// ordinary <see cref="AuthOperationResult"/> error envelope so a failed
/// mutation can never accidentally serialize a credential.
/// </summary>
public sealed class SecurityMutationResponse
{
    public AuthOperationResult Result { get; private init; } = AuthOperationResult.Success();
    public TokenPairResult? Tokens { get; private init; }
    public bool RequiresRelogin { get; private init; }

    public bool Succeeded => Result.Succeeded;
    public IReadOnlyCollection<AuthOperationError> Errors => Result.Errors;

    public static SecurityMutationResponse Success(TokenPairResult? tokens = null)
        => new()
        {
            Result = AuthOperationResult.Success(),
            Tokens = tokens,
            RequiresRelogin = tokens is null,
        };

    public static SecurityMutationResponse Fail(string code, string description)
        => new()
        {
            Result = AuthOperationResult.Fail(code, description),
            Tokens = null,
            RequiresRelogin = false,
        };

    public static SecurityMutationResponse From(AuthOperationResult result)
        => new()
        {
            Result = result,
            RequiresRelogin = result.Succeeded,
        };
}
