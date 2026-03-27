using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ChatApp.Server.Middlewares;
using Core.Models.DTOs.Auth;
using Infrastructure.Extensions;
using Infrastructure.Models.Config;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using NLog.Extensions.Logging;

namespace ChatApp.Server;

public abstract class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddNLog();

        // 添加控制器支持，并统一使用 camelCase 输出 JSON。
        builder.Services.AddControllers().AddJsonOptions(op =>
        {
            op.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        // 注册 HttpContext 访问器，便于服务层按需读取当前请求信息。
        builder.Services.AddHttpContextAccessor();

        // 从配置中读取 JWT 设置。
        var config = builder.Configuration;
        var jwtSettings = config.GetSection("JwtSettings");
        var emailSettings = config.GetSection("EmailSettings");

        // 添加 Redis 缓存服务。
        var service = await builder.Services.AddRedisCacheServices(config).ConfigureAwait(false);

        // 添加数据库上下文、Identity 和业务服务。
        service.AddUserDbContext(config)
            .AddCoreServiceCollection();

        service.Configure<JwtSettings>(jwtSettings).AddOptions<JwtSettings>();
        service.Configure<EmailConfig>(emailSettings).AddOptions<EmailConfig>();

        var jwtOptions = jwtSettings.Get<JwtSettings>() ?? throw new ArgumentException("JWT 未配置");

        service.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                // 是否验证颁发者。
                ValidateIssuer = true,
                // 当前项目暂不校验受众。
                ValidateAudience = false,
                // 是否验证签名。
                ValidateIssuerSigningKey = true,
                // 是否验证过期时间。
                ValidateLifetime = true,
                // 指定名称和角色声明的读取方式。
                NameClaimType = ClaimTypes.NameIdentifier,
                RoleClaimType = ClaimTypes.Role,
                ValidIssuer = jwtOptions.Issuer,
                ValidAudience = jwtOptions.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Secret))
            };
        });

        // 配置 CORS 策略，允许配置文件中声明的来源访问。
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowSpecific", policy =>
            {
                policy.WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .AllowCredentials();
            });
        });

        var app = builder.Build();

        // 使用全局异常处理中间件。
        app.UseMiddleware<ExceptionHandlingMiddleware>();

        // 应用跨域配置。
        app.UseCors("AllowSpecific");

        // 强制 HTTPS。
        app.UseHttpsRedirection();

        // 先认证，再授权。
        app.UseAuthentication();
        app.UseAuthorization();

        // 映射控制器路由。
        app.MapControllers();

        await app.RunAsync().ConfigureAwait(false);
    }
}