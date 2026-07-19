namespace Core.Settings;

public sealed class RealtimeGatewayOptions
{
    public const string SectionName = "RealtimeGateway";

    public string Host { get; init; } = "127.0.0.1";
    public ushort Port { get; init; } = 8888;
    public string Name { get; init; } = "ChatApp.TcpGateway";
}
