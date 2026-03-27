namespace Core.Models.DTOs.Auth;

public class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Secret { get; init; } = string.Empty;
    public string Issuer { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public short AccessTokenExpirationMinutes { get; init; } = 60;
    public byte RefreshTokenLength { get; init; } = 32;

    public int RefreshTokenExpirationDays { get; set; } = 3;
}