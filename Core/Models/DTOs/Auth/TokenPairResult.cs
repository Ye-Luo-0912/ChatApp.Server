namespace Core.Models.DTOs.Auth;

public struct TokenPairResult()
{
    public bool IsSuccess { get; private set; } = false;
    public string? AccessToken { get; set; } = null;
    public string? RefreshToken { get; set; } = null;
    public AuthErrorType? ErrorType { get; private set; } = null;
    
    public static TokenPairResult Success(string? accessToken, string? refreshToken) 
        => new() { IsSuccess = true, AccessToken = accessToken, RefreshToken = refreshToken };
    
    public static TokenPairResult Fail(AuthErrorType errorType) 
        => new() { IsSuccess = false, AccessToken = null, RefreshToken = null, ErrorType = errorType };
    
}