using Core.Settings;

namespace Core.Models.Token;

public struct ServerEndPoint
{
    public string Host { get; set; }
    public string Name {get;set; }
    public ushort Port { get; set; }

    /// <summary>端点传输 scheme（可选，镜像 Shared EndpointScheme）；null = 旧形状，不下发元数据。</summary>
    public GatewayTransportScheme? Scheme { get; set; }

    /// <summary>最低 TLS 策略（可选，镜像 Shared MinimumTlsPolicy）；null = 不下发，消费者保持旧行为。</summary>
    public GatewayMinimumTlsPolicy? MinimumTls { get; set; }

    /// <summary>TLS SNI 覆盖（可选）；null/空白 = 回退 Host。</summary>
    public string? SniTargetHost { get; set; }
}
