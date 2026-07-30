using Core.Interfaces;
using Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Extensions;

/// <summary>
/// 好友关系模块的组合根：好友服务注册。
/// </summary>
public static class FriendshipModuleExtensions
{
    /// <summary>注册好友关系服务。</summary>
    public static IServiceCollection AddFriendshipModule(this IServiceCollection services)
    {
        services.AddScoped<IFriendshipService, FriendshipService>();
        return services;
    }
}
