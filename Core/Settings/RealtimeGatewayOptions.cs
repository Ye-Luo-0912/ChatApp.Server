namespace Core.Settings;

/// <summary>
/// 端点传输 scheme 的 Core 侧镜像枚举，数值与 Shared <c>EndpointScheme</c> 一一对应
/// （Http=1/Https=2/Tcp=3/TcpTls=4；0=未声明，不下发）。Core 不引用 transport 契约包，
/// 由 Host 侧 mapper 做数值对齐的转换。
/// </summary>
public enum GatewayTransportScheme : byte
{
    Unspecified = 0,
    Http = 1,
    Https = 2,
    Tcp = 3,
    TcpTls = 4
}

/// <summary>
/// 最低 TLS 版本策略的 Core 侧镜像枚举，数值与 Shared <c>MinimumTlsPolicy</c> 一一对应
/// （None=0/Tls12OrAbove=1/Tls13Only=2）。0 值即"明示无最低版本"，null 才是不下发。
/// </summary>
public enum GatewayMinimumTlsPolicy : byte
{
    None = 0,
    Tls12OrAbove = 1,
    Tls13Only = 2
}

public sealed class RealtimeGatewayOptions
{
    public const string SectionName = "RealtimeGateway";

    public string Host { get; init; } = "127.0.0.1";
    public ushort Port { get; init; } = 8888;
    public string Name { get; init; } = "ChatApp.TcpGateway";

    /// <summary>
    /// 网关端点传输 scheme（可选）。缺省 = 不在登录响应里下发端点安全元数据，
    /// wire 保持旧形状（host/name/port）；明文部署不会因此被自动"变严"。
    /// </summary>
    public GatewayTransportScheme? Scheme { get; init; }

    /// <summary>最低 TLS 版本策略（可选）。缺省 = 不下发，消费者保持其平台默认。</summary>
    public GatewayMinimumTlsPolicy? MinimumTls { get; init; }

    /// <summary>TLS SNI / 目标主机覆盖（可选）。空白视为未设置，不下发。</summary>
    public string? SniTargetHost { get; init; }

    /// <summary>
    /// 配置一致性校验：枚举值必须已定义，且明文 scheme（Http/Tcp）不得声明 TLS policy
    /// ——与 Shared EndpointPolicy 的 <c>PlaintextSchemeWithTlsPolicy</c> 规则一致，
    /// 避免服务端生产出会被新客户端 fail-closed 拒绝的下发形状。
    /// </summary>
    public bool IsEndpointMetadataConsistent(out string? error)
    {
        if (Scheme is { } scheme && !Enum.IsDefined(scheme))
        {
            error = $"RealtimeGateway:Scheme 值无效：{(byte)scheme} 不是已定义的传输 scheme。";
            return false;
        }

        if (MinimumTls is { } minimumTls && !Enum.IsDefined(minimumTls))
        {
            error = $"RealtimeGateway:MinimumTls 值无效：{(byte)minimumTls} 不是已定义的 TLS 策略。";
            return false;
        }

        if (Scheme is GatewayTransportScheme.Http or GatewayTransportScheme.Tcp
            && MinimumTls is not null)
        {
            error = "RealtimeGateway 配置不一致：明文 scheme（Http/Tcp）不能声明最低 TLS 策略。";
            return false;
        }

        error = null;
        return true;
    }
}
