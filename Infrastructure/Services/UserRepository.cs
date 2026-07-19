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
    public async Task<ApplicationUser?> FindByIdAsync(long userId, CancellationToken cancellationToken = default) =>
        await db.Users.FindAsync([userId], cancellationToken);

    public async Task<ApplicationUser?> FindByNameAsync(string username, CancellationToken cancellationToken = default)
    {
        var normalized = username.ToUpperInvariant();
        return await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized, cancellationToken);
    }

    public async Task<bool> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        db.Users.Update(user);
        return await db.SaveChangesAsync(cancellationToken) > 0;
    }

    public async Task<bool> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        db.Users.Remove(user);
        return await db.SaveChangesAsync(cancellationToken) > 0;
    }
}
