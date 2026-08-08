namespace Core.Settings;

public sealed class PhoneVerificationOptions
{
    public const string SectionName = "PhoneVerification";
    public string? WebhookUrl { get; set; }
    public string? AuthorizationToken { get; set; }
    public int CodeLifetimeMinutes { get; set; } = 5;
    public int ResendCooldownSeconds { get; set; } = 60;
}
