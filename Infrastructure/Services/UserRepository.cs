using Core.Interfaces;
using Core.Models.Identity;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>
/// 基于 EF Core 的用户数据访问实现。
/// </summary>
public class UserRepository(UserDbContext db) : IUserRepository
{
    public async Task<ApplicationUser?> FindByIdAsync(long userId) =>
        await db.Users.FindAsync(userId);

    public async Task<ApplicationUser?> FindByNameAsync(string username)
    {
        var normalized = username.ToUpperInvariant();
        return await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized);
    }

    public async Task<bool> UpdateAsync(ApplicationUser user)
    {
        db.Users.Update(user);
        return await db.SaveChangesAsync() > 0;
    }

    public async Task<bool> DeleteAsync(ApplicationUser user)
    {
        db.Users.Remove(user);
        return await db.SaveChangesAsync() > 0;
    }
}
