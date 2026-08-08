using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models.Common;
using Core.Models.Identity;
using Core.Models.Security;
using Core.Models.User;
using Infrastructure.Data;
using Infrastructure.Services.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Services;

/// <summary>
/// 基于 EF Core 的用户数据访问实现。
/// </summary>
public class UserRepository(
    UserDbContext db,
    ITsidGenerator tsidGenerator,
    ISecurityMutationCoordinator? securityMutations = null) : IUserRepository
{
    private readonly ISecurityMutationCoordinator _securityMutationCoordinator =
        securityMutations ?? new SecurityMutationCoordinator(
            db,
            new SecurityVersionAdvancer(db),
            NullLogger<SecurityMutationCoordinator>.Instance);

    public async Task<ApplicationUser?> FindByIdAsync(long userId, CancellationToken cancellationToken = default) =>
        await db.Users.FindAsync([userId], cancellationToken);

    public async Task<ApplicationUser?> FindByNameAsync(string username, CancellationToken cancellationToken = default)
    {
        var normalized = username.ToUpperInvariant();
        var now = DateTimeOffset.UtcNow;
        return await db.Users.FirstOrDefaultAsync(
            u => u.NormalizedUserName == normalized
                 && u.AllowBeSearched
                 && !(u.LockoutEnabled && u.LockoutEnd != null && u.LockoutEnd > now),
            cancellationToken);
    }

    /// <summary>内部查找（含禁用用户），供账户自身操作使用。</summary>
    public async Task<ApplicationUser?> FindByNameIncludingDisabledAsync(
        string username, CancellationToken cancellationToken = default)
    {
        var normalized = username.ToUpperInvariant();
        return await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized, cancellationToken);
    }

    public async Task<ApplicationUser?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = email.Trim().ToUpperInvariant();
        return await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
    }

    public Task<bool> IsEmailTakenAsync(string normalizedEmail, long? excludeUserId = null, CancellationToken cancellationToken = default)
    {
        var query = db.Users.AsNoTracking().Where(u => u.NormalizedEmail == normalizedEmail);
        if (excludeUserId is { } id)
            query = query.Where(u => u.Id != id);
        return query.AnyAsync(cancellationToken);
    }

    public Task<bool> IsUserNameTakenAsync(string normalizedUserName, long? excludeUserId = null, CancellationToken cancellationToken = default)
    {
        var query = db.Users.AsNoTracking().Where(u => u.NormalizedUserName == normalizedUserName);
        if (excludeUserId is { } id)
            query = query.Where(u => u.Id != id);
        return query.AnyAsync(cancellationToken);
    }

    public async Task<bool> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        // Repository queries return tracked users. Let EF persist only changed columns;
        // Update(user) would mark the whole wide row modified and overwrite unrelated concurrent changes.
        db.ChangeTracker.DetectChanges();
        return await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    // ── 用户搜索：%keyword% 依赖 pg_trgm GIN（见 migration AddPgTrgmSearchIndexes）
    public async Task<CursorPage<PublicUserSearchResult>> SearchUsersAsync(
        string searchTerm, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(limit, 1, 50);
        var safe = searchTerm.Replace("%", @"\%").Replace("_", @"\_");
        var normalizedTerm = searchTerm.Trim().ToUpperInvariant();
        // 前缀优先走 UserName btree；contains 由 gin_trgm_ops 加速
        var prefix = $"{safe}%";
        var contains = $"%{safe}%";
        var hasCursor = SearchCursor.TryDecode(cursor, out var searchCursor);
        var now = DateTimeOffset.UtcNow;

        var query = db.Users.AsNoTracking()
            .Where(u => u.AllowBeSearched)
            .Where(u => !(u.LockoutEnabled && u.LockoutEnd != null && u.LockoutEnd > now))
            .Where(u => u.BanUntil == null || u.BanUntil <= now)
            .Where(u => u.DeletionScheduledAt == null || u.DeletionScheduledAt > now)
            .Where(u => u.UserName != null
                        && (EF.Functions.ILike(u.UserName, prefix)
                            || EF.Functions.ILike(u.UserName, contains)));

        var scored = query.Select(u => new
        {
            User = u,
            Score = u.NormalizedUserName == normalizedTerm
                ? 3
                : u.NormalizedUserName != null && u.NormalizedUserName.StartsWith(normalizedTerm)
                    ? 2
                    : 1,
        });
        if (hasCursor)
        {
            scored = scored.Where(x => x.Score < searchCursor.Score
                                       || (x.Score == searchCursor.Score && x.User.Id > searchCursor.Id));
        }

        var rows = await scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.User.Id)
            .Take(pageSize + 1)
            .Select(x => new PublicUserSearchResult
            {
                Id = x.User.Id,
                UserName = x.User.UserName,
                AvatarUrl = x.User.AvatarUrl,
                Signature = x.User.Signature,
                RelevanceScore = x.Score,
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        return new CursorPage<PublicUserSearchResult>
        {
            Items = rows,
            HasMore = hasMore,
            NextCursor = hasMore && rows.Count > 0
                ? new SearchCursor(rows[^1].RelevanceScore, rows[^1].Id).Encode()
                : null,
        };
    }

    public async Task<CursorPage<DisabledUserDto>> ListDisabledUsersAsync(
        string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(limit, 1, 100);
        long? cursorId = long.TryParse(cursor, out var c) ? c : null;
        var now = DateTimeOffset.UtcNow;

        var query = db.Users.AsNoTracking()
            .Where(u => u.LockoutEnabled && u.LockoutEnd != null && u.LockoutEnd > now);

        if (cursorId.HasValue)
            query = query.Where(u => u.Id > cursorId.Value);

        var rows = await query
            .OrderBy(u => u.Id)
            .Take(pageSize + 1)
            .Select(u => new DisabledUserDto
            {
                Id = u.Id,
                UserName = u.UserName,
                Email = u.Email,
                LockoutEnd = u.LockoutEnd,
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        return new CursorPage<DisabledUserDto>
        {
            Items = rows,
            HasMore = hasMore,
            NextCursor = hasMore && rows.Count > 0 ? rows[^1].Id.ToString() : null,
        };
    }

    public async Task<IReadOnlyList<string>> GetRoleNamesAsync(long userId, CancellationToken cancellationToken = default)
    {
        return await db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name!)
            .Where(n => n != null)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountUsersInRoleAsync(string roleName, CancellationToken cancellationToken = default)
    {
        var normalized = roleName.Trim().ToUpperInvariant();
        return await db.UserRoles.AsNoTracking()
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.NormalizedName })
            .Where(x => x.NormalizedName == normalized)
            .Select(x => x.UserId)
            .Distinct()
            .CountAsync(cancellationToken);
    }

    public async Task<RoleMutationOutcome> MutateRoleAsync(
        long userId,
        string roleName,
        bool assign,
        long actorUserId,
        string? reason,
        string? clientIp,
        CancellationToken cancellationToken = default)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
                return RoleMutationOutcome.UserNotFound;

            var normalized = roleName.Trim().ToUpperInvariant();
            var role = await db.Roles.FirstOrDefaultAsync(r => r.NormalizedName == normalized, cancellationToken);
            if (role is null)
            {
                if (!KnownRoles.IsAssignable(roleName))
                    return RoleMutationOutcome.RoleNotFound;

                role = new ApplicationRoles
                {
                    Id = tsidGenerator.GenerateTsid(),
                    Name = KnownRoles.Normalize(roleName),
                    NormalizedName = normalized,
                };
                db.Roles.Add(role);
                await db.SaveChangesAsync(cancellationToken);
            }

            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

            if (string.Equals(
                    normalized,
                    KnownRoles.Admin.ToUpperInvariant(),
                    StringComparison.Ordinal))
            {
                await AdminRoleInvariant.AcquireMutationLockAsync(db, cancellationToken);
            }

            if (assign)
            {
                if (await db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == role.Id, cancellationToken))
                {
                    await tx.CommitAsync(cancellationToken);
                    return RoleMutationOutcome.AlreadyHasRole;
                }

                db.UserRoles.Add(new UserRole { UserId = userId, RoleId = role.Id });
            }
            else
            {
                var link = await db.UserRoles.FirstOrDefaultAsync(
                    ur => ur.UserId == userId && ur.RoleId == role.Id, cancellationToken);
                if (link is null)
                {
                    await tx.CommitAsync(cancellationToken);
                    return RoleMutationOutcome.RoleNotAssigned;
                }

                if (string.Equals(normalized, KnownRoles.Admin.ToUpperInvariant(), StringComparison.Ordinal))
                {
                    if (await AdminRoleInvariant.IsLastActiveAdminAsync(
                            db, userId, cancellationToken))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return RoleMutationOutcome.LastAdmin;
                    }
                }

                db.UserRoles.Remove(link);
            }

            user.SecurityStamp = Guid.NewGuid().ToString();

            db.AdminAuditLogs.Add(new AdminAuditLog
            {
                AdminUserId = actorUserId,
                TargetUserId = userId,
                Action = assign ? "AssignRole" : "RemoveRole",
                Reason = reason,
                Detail = role.Name,
                ClientIp = clientIp,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            var mutation = await _securityMutationCoordinator.ExecuteAsync(
                    userId,
                    assign ? SecurityEventType.RoleAssigned : SecurityEventType.RoleRemoved,
                    role.Name,
                    static _ => Task.CompletedTask,
                    cancellationToken,
                    securityEvent =>
                    {
                        securityEvent.ActorUserId = actorUserId.ToString();
                        securityEvent.ClientIp = clientIp;
                    })
                .ConfigureAwait(false);
            if (!mutation.Succeeded)
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return RoleMutationOutcome.SecurityVersionFailed;
            }

            await tx.CommitAsync(cancellationToken);
            return RoleMutationOutcome.Success;
        });
    }

    public async Task AddAdminAuditAsync(AdminAuditLog log, CancellationToken cancellationToken = default)
    {
        db.AdminAuditLogs.Add(log);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<CursorPage<SecurityEventDto>> ListSecurityEventsAsync(
        long userId, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(limit, 1, 100);
        long? cursorId = long.TryParse(cursor, out var c) ? c : null;

        var query = db.SecurityEvents.AsNoTracking().Where(e => e.UserId == userId);
        if (cursorId.HasValue)
            query = query.Where(e => e.Id < cursorId.Value);

        var rows = await query
            .OrderByDescending(e => e.Id)
            .Take(pageSize + 1)
            .Select(e => new SecurityEventDto
            {
                Id = e.Id,
                EventType = e.EventType,
                DeviceId = e.DeviceId,
                SessionId = e.SessionId,
                ClientIp = e.ClientIp,
                Location = e.Location,
                Detail = e.Detail,
                CreatedAt = e.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        return new CursorPage<SecurityEventDto>
        {
            Items = rows,
            HasMore = hasMore,
            NextCursor = hasMore && rows.Count > 0 ? rows[^1].Id.ToString() : null,
        };
    }

    public Task<SecurityEventDto?> GetSecurityEventAsync(
        long userId, long eventId, CancellationToken cancellationToken = default)
        => db.SecurityEvents
            .AsNoTracking()
            .Where(e => e.UserId == userId && e.Id == eventId)
            .Select(e => new SecurityEventDto
            {
                Id = e.Id,
                EventType = e.EventType,
                DeviceId = e.DeviceId,
                SessionId = e.SessionId,
                ClientIp = e.ClientIp,
                Location = e.Location,
                Detail = e.Detail,
                CreatedAt = e.CreatedAt,
            })
            .SingleOrDefaultAsync(cancellationToken);
}
