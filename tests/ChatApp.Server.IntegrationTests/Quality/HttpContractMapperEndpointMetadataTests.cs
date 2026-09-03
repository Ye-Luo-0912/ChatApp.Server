using System.Text.Json;
using ChatApp.Contracts.Http;
using ChatApp.Contracts.Http.Auth;
using ChatApp.Contracts.Http.Common;
using Core.Models.Token;
using Core.Settings;
using ChatApp.Server.Models;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Quality;

/// <summary>
/// ENDPOINT-TLS-1 生产者侧：登录响应 <c>Server</c> 端点元数据的下发形状与兼容语义。
/// wire 是加性扩展——未配置元数据时响应与旧形状逐字节一致；配置后新增
/// scheme/sniTargetHost/minimumTls 三字段，旧客户端跳过未知字段不受影响。
/// </summary>
public sealed class HttpContractMapperEndpointMetadataTests
{
    private static LoginResult CreateLoginResult(ServerEndPoint? server) => new()
    {
        IsSuccess = true,
        LoginCheckStatus = Core.Models.Token.LoginCheckStatus.Success,
        UserId = 42,
        AccessToken = "access",
        AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
        RefreshToken = "refresh",
        RefreshTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
        Server = server,
    };

    private static string Serialize(LoginResponse response) =>
        JsonSerializer.Serialize(response, HttpContractsJsonSerializerContext.Default.LoginResponse);

    [Fact]
    public void MirrorEnums_AlignNumericallyWithContractEnums()
    {
        // Core 不引用 transport 契约包，mapper 依赖数值对齐转换，这里把对齐钉死。
        Assert.Equal((byte)ChatApp.Contracts.Http.Common.EndpointScheme.Http, (byte)GatewayTransportScheme.Http);
        Assert.Equal((byte)ChatApp.Contracts.Http.Common.EndpointScheme.Https, (byte)GatewayTransportScheme.Https);
        Assert.Equal((byte)ChatApp.Contracts.Http.Common.EndpointScheme.Tcp, (byte)GatewayTransportScheme.Tcp);
        Assert.Equal((byte)ChatApp.Contracts.Http.Common.EndpointScheme.TcpTls, (byte)GatewayTransportScheme.TcpTls);
        Assert.Equal((byte)ChatApp.Contracts.Http.Common.MinimumTlsPolicy.None, (byte)GatewayMinimumTlsPolicy.None);
        Assert.Equal((byte)ChatApp.Contracts.Http.Common.MinimumTlsPolicy.Tls12OrAbove, (byte)GatewayMinimumTlsPolicy.Tls12OrAbove);
        Assert.Equal((byte)ChatApp.Contracts.Http.Common.MinimumTlsPolicy.Tls13Only, (byte)GatewayMinimumTlsPolicy.Tls13Only);
    }

    [Fact]
    public void LegacyServerEndPoint_MapsToLegacyWireShape_ByteForByte()
    {
        // 缺省部署（未配置端点元数据）：下发 JSON 必须与 0.3.0 时代逐字节一致。
        var login = CreateLoginResult(new ServerEndPoint
        {
            Host = "127.0.0.1",
            Name = "ChatApp.TcpGateway",
            Port = 8888,
        });

        LoginResponse response = login.ToHttpContract();

        Assert.NotNull(response.Server);
        Assert.Equal("127.0.0.1", response.Server.Value.Host);
        Assert.Equal("ChatApp.TcpGateway", response.Server.Value.Name);
        Assert.Equal((ushort)8888, response.Server.Value.Port);
        Assert.Null(response.Server.Value.Scheme);
        Assert.Null(response.Server.Value.MinimumTls);
        Assert.Null(response.Server.Value.SniTargetHost);
        Assert.Contains(
            """{"host":"127.0.0.1","name":"ChatApp.TcpGateway","port":8888}""",
            Serialize(response),
            StringComparison.Ordinal);
        // 旧形状之外不允许出现任何端点安全字段键。
        Assert.DoesNotContain("\"scheme\"", Serialize(response), StringComparison.Ordinal);
        Assert.DoesNotContain("\"minimumTls\"", Serialize(response), StringComparison.Ordinal);
        Assert.DoesNotContain("\"sniTargetHost\"", Serialize(response), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingServer_MapsToNull()
    {
        LoginResponse response = CreateLoginResult(null).ToHttpContract();

        Assert.Null(response.Server);
        Assert.DoesNotContain("\"server\"", Serialize(response), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredMetadata_MapsFieldByFieldOntoAdditiveWire()
    {
        var login = CreateLoginResult(new ServerEndPoint
        {
            Host = "10.0.0.8",
            Name = "cn-1",
            Port = 7000,
            Scheme = GatewayTransportScheme.TcpTls,
            MinimumTls = GatewayMinimumTlsPolicy.Tls12OrAbove,
            SniTargetHost = "gw.example.com",
        });

        LoginResponse response = login.ToHttpContract();

        Assert.NotNull(response.Server);
        ServerEndpoint server = response.Server.Value;
        Assert.Equal("10.0.0.8", server.Host);
        Assert.Equal("cn-1", server.Name);
        Assert.Equal((ushort)7000, server.Port);
        Assert.Equal(ChatApp.Contracts.Http.Common.EndpointScheme.TcpTls, server.Scheme);
        Assert.Equal(ChatApp.Contracts.Http.Common.MinimumTlsPolicy.Tls12OrAbove, server.MinimumTls);
        Assert.Equal("gw.example.com", server.SniTargetHost);

        // 逐字段断言下发 JSON 形状：camelCase + 数值枚举 + 新字段只在配置后出现。
        Assert.Contains(
            """{"host":"10.0.0.8","name":"cn-1","port":7000,"scheme":4,"sniTargetHost":"gw.example.com","minimumTls":1}""",
            Serialize(response),
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlaintextDeployment_WithExplicitSchemeOnly_DoesNotInventTlsPolicy()
    {
        // 开发环境明文部署：配置了 scheme=Tcp 但未配置 TLS 策略 → 不得自动"变严"，
        // 下发的 minimumTls 缺省（旧客户端无感，新客户端按 None=平台默认处理）。
        var login = CreateLoginResult(new ServerEndPoint
        {
            Host = "gw.local",
            Name = "dev",
            Port = 7000,
            Scheme = GatewayTransportScheme.Tcp,
        });

        LoginResponse response = login.ToHttpContract();

        Assert.NotNull(response.Server);
        Assert.Equal(ChatApp.Contracts.Http.Common.EndpointScheme.Tcp, response.Server.Value.Scheme);
        Assert.Null(response.Server.Value.MinimumTls);
        Assert.Null(response.Server.Value.SniTargetHost);
        Assert.Contains(
            """{"host":"gw.local","name":"dev","port":7000,"scheme":3}""",
            Serialize(response),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GatewayTransportScheme.Tcp, GatewayMinimumTlsPolicy.Tls12OrAbove)]
    [InlineData(GatewayTransportScheme.Tcp, GatewayMinimumTlsPolicy.Tls13Only)]
    [InlineData(GatewayTransportScheme.Http, GatewayMinimumTlsPolicy.Tls12OrAbove)]
    public void InsecureCombination_IsRejectedAtConfigurationValidation(
        GatewayTransportScheme scheme,
        GatewayMinimumTlsPolicy minimumTls)
    {
        var options = new RealtimeGatewayOptions { Scheme = scheme, MinimumTls = minimumTls };

        Assert.False(options.IsEndpointMetadataConsistent(out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void UndefinedEnumValues_AreRejectedByConfigurationValidation()
    {
        var options = new RealtimeGatewayOptions
        {
            Scheme = (GatewayTransportScheme)99,
            MinimumTls = GatewayMinimumTlsPolicy.Tls12OrAbove,
        };

        Assert.False(options.IsEndpointMetadataConsistent(out _));

        var invalidTls = new RealtimeGatewayOptions
        {
            Scheme = GatewayTransportScheme.TcpTls,
            MinimumTls = (GatewayMinimumTlsPolicy)42,
        };

        Assert.False(invalidTls.IsEndpointMetadataConsistent(out _));
    }

    [Theory]
    [InlineData(GatewayTransportScheme.TcpTls, GatewayMinimumTlsPolicy.Tls12OrAbove)]
    [InlineData(GatewayTransportScheme.TcpTls, null)]
    [InlineData(GatewayTransportScheme.Tcp, null)]
    [InlineData(null, null)]
    public void ConsistentConfigurations_PassValidation(GatewayTransportScheme? scheme, GatewayMinimumTlsPolicy? minimumTls)
    {
        var options = new RealtimeGatewayOptions { Scheme = scheme, MinimumTls = minimumTls };

        Assert.True(options.IsEndpointMetadataConsistent(out string? error));
        Assert.Null(error);
    }
}
