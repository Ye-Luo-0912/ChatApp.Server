using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Core.Models.Identity;
using Infrastructure.Data;
using Infrastructure.Services.Auth;
using Infrastructure.Services.Utilities;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace ChatApp.Server.IntegrationTests.Support;

internal static class WafTestHelpers
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static HttpClient CreateClientWithDevice(this ChatAppWebApplicationFactory factory, string deviceId)
    {
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Add("X-Device-Id", deviceId);
        return client;
    }

    public static async Task<ApplicationUser> SeedUserAsync(
        UserDbContext db, string name, string email, string password, bool allowSearch = true)
    {
        var hasher = new BcryptPasswordHasher();
        var user = new ApplicationUser
        {
            Id = new TsidGeneratorService().GenerateTsid(),
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            PasswordHash = await hasher.HashPasswordAsync(password),
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
            AllowBeSearched = allowSearch,
            FriendRequestPolicy = FriendRequestPolicy.RequireVerification,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public static async Task EnsureRoleAsync(UserDbContext db, string roleName)
    {
        var normalized = roleName.ToUpperInvariant();
        if (await db.Roles.AnyAsync(r => r.NormalizedName == normalized))
            return;

        db.Roles.Add(new ApplicationRoles
        {
            Id = new TsidGeneratorService().GenerateTsid(),
            Name = roleName,
            NormalizedName = normalized,
        });
        await db.SaveChangesAsync();
    }

    public static async Task AssignRoleAsync(UserDbContext db, long userId, string roleName)
    {
        await EnsureRoleAsync(db, roleName);
        var role = await db.Roles.SingleAsync(r => r.NormalizedName == roleName.ToUpperInvariant());
        if (!await db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == role.Id))
        {
            db.UserRoles.Add(new UserRole { UserId = userId, RoleId = role.Id });
            await db.SaveChangesAsync();
        }
    }

    public static async Task<LoginDto> LoginAsync(HttpClient client, string username, string password)
    {
        var res = await client.PostAsJsonAsync("/api/auth/login", new { username, password }, Json);
        var raw = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Login failed {(int)res.StatusCode}: {raw}");
        var body = JsonSerializer.Deserialize<LoginDto>(raw, Json);
        return body ?? throw new InvalidOperationException("login body null");
    }

    public static void UseBearer(this HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    public static byte[] CreateJpeg(int width, int height)
    {
        using var image = new Image<Rgb24>(width, height);
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = 80 });
        return ms.ToArray();
    }

    public sealed class LoginDto
    {
        public bool IsSuccess { get; set; }
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public long? UserId { get; set; }
        public bool RequiresTwoFactor { get; set; }
    }
}
