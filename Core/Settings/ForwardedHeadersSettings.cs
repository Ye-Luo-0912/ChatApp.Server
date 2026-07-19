using System.Net;

namespace Core.Settings;

/// <summary>
/// 可信反向代理配置。未配置任何代理/网段时，不信任客户端伪造的 X-Forwarded-For。
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>可信代理 IP，如 "127.0.0.1"、"10.0.0.2"。</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>可信代理 CIDR，如 "10.0.0.0/8"、"172.16.0.0/12"。</summary>
    public string[] KnownNetworks { get; set; } = [];
}
