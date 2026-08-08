namespace Core.Models.Token;

/// <summary>
/// Core projection of a login session. This is not an HTTP wire DTO; the Host
/// maps it to the versioned <c>ChatApp.Contracts.Http.Sessions.SessionDevice</c>.
/// </summary>
public sealed class SessionDeviceProjection
{
    public string DeviceId { get; init; } = string.Empty;
    public string? DeviceName { get; init; }
    public string? DeviceType { get; init; }
    public string? ClientIp { get; init; }
    public string? UserAgent { get; init; }
    public DateTime LoginAt { get; init; }
    public DateTime LastActiveAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public string? SessionId { get; init; }
    public int RefreshCount { get; init; }
    public bool IsCurrent { get; init; }

    public static SessionDeviceProjection From(SessionRecord session, bool isCurrent) => new()
    {
        DeviceId = session.DeviceId,
        DeviceName = session.DeviceName,
        DeviceType = session.DeviceType,
        ClientIp = session.ClientIp,
        UserAgent = session.UserAgent,
        LoginAt = session.LoginAt,
        LastActiveAt = session.LastActiveAt,
        ExpiresAt = session.ExpiresAt,
        SessionId = session.SessionId,
        RefreshCount = session.RefreshCount,
        IsCurrent = isCurrent,
    };
}
