using Core.Models.Auth;

namespace Core.Models.Token;

public struct TokenPairResult()
{
    public bool IsSuccess { get; private set; } = false;
    public string? AccessToken { get; set; } = null;
    public DateTime AccessTokenExpiresAtUtc { get; set; }
    public string? RefreshToken { get; set; } = null;
    public DateTime RefreshTokenExpiresAtUtc { get; set; }
    public string? DeviceCredential { get; set; } = null;
    public AuthErrorType? ErrorType { get; private set; } = null;
    
    public static TokenPairResult Success(
        string accessToken,
        DateTime accessTokenExpiresAtUtc,
        string refreshToken,
        DateTime refreshTokenExpiresAtUtc,
        string? deviceCredential = null)
        => new()
        {
            IsSuccess = true,
            AccessToken = accessToken,
            AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc,
            RefreshToken = refreshToken,
            RefreshTokenExpiresAtUtc = refreshTokenExpiresAtUtc,
            DeviceCredential = deviceCredential,
        };
    
    public static TokenPairResult Fail(AuthErrorType errorType) 
        => new() { IsSuccess = false, AccessToken = null, RefreshToken = null, DeviceCredential = null, ErrorType = errorType };
    
}
