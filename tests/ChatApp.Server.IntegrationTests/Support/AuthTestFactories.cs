using System.Security.Cryptography;
using System.Text;
using Core.Interfaces;
using Core.Settings;
using Infrastructure.Services.Auth;
using Microsoft.Extensions.Options;

namespace ChatApp.Server.IntegrationTests.Support;

internal static class AuthTestFactories
{
    public static IAuthCpuLimiter CreateCpuLimiter(
        int maxConcurrent = 4, int acquireTimeoutMs = 200)
        => new AuthCpuLimiter(Options.Create(new PasswordHashingOptions
        {
            MaxConcurrentOperations = maxConcurrent,
            AcquireTimeoutMilliseconds = acquireTimeoutMs,
        }));

    public static BcryptPasswordHasher CreatePasswordHasher(
        int maxConcurrent = 4, int acquireTimeoutMs = 200)
        => new(CreateCpuLimiter(maxConcurrent, acquireTimeoutMs));

    /// <summary>生成合法长度的稳定设备 ID。</summary>
    public static string StableDeviceId(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
