using Core.Interfaces;
using Core.Services;
using Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Extensions;

/// <summary>
/// 注册基础设施层和核心业务层所需的服务。
/// </summary>
public static class AddServices
{
    /// <summary>
    /// 注册当前项目使用到的核心服务。
    /// </summary>
    public static void AddCoreServiceCollection(this IServiceCollection services)
    {
        services.AddSingleton<IDeviceInfo, DeviceInfoService>();
        services.AddSingleton<IJwtTokenService, JwtTokenServices>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserAccountService, UserAccountService>();
        services.AddScoped<IFriendshipService, FriendshipService>();
        services.AddSingleton<IEmailSender, EmailService>();
        services.AddSingleton<IEmailVerificationService, EmailVerificationService>();
    }
}