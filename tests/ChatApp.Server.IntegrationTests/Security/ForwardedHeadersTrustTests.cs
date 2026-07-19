using System.Net;
using Core.Interfaces;
using Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Security;

/// <summary>
/// DeviceInfo 只读取经中间件处理后的 RemoteIpAddress，不再直接信任 X-Forwarded-For。
/// </summary>
public sealed class ForwardedHeadersTrustTests
{
    [Fact]
    public void ForgedXForwardedFor_Header_DoesNotOverrideRemoteIp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<IDeviceInfo, DeviceInfoService>();

        using var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        var deviceInfo = provider.GetRequiredService<IDeviceInfo>();

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        context.Request.Headers["X-Forwarded-For"] = "1.2.3.4";
        context.Request.Headers["X-Real-IP"] = "1.2.3.4";
        context.Request.Headers["CF-Connecting-IP"] = "1.2.3.4";
        context.Request.Headers.UserAgent = "IntegrationTestAgent/1.0";
        accessor.HttpContext = context;

        var info = deviceInfo.GenerateDeviceInfo();
        Assert.Equal("203.0.113.10", info.IpAddress);
    }

    [Fact]
    public void TrustedProxyResult_UsesRemoteIpAfterMiddleware()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<IDeviceInfo, DeviceInfoService>();

        using var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        var deviceInfo = provider.GetRequiredService<IDeviceInfo>();

        var context = new DefaultHttpContext();
        // 模拟 ForwardedHeadersMiddleware 已用可信代理的 XFF 覆盖 RemoteIpAddress
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.20");
        context.Request.Headers.UserAgent = "IntegrationTestAgent/1.0";
        accessor.HttpContext = context;

        var info = deviceInfo.GenerateDeviceInfo();
        Assert.Equal("198.51.100.20", info.IpAddress);
    }
}
