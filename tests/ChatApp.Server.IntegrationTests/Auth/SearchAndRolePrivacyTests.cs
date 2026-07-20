using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
using Core.Models.Identity;
using Infrastructure.Services;
using Infrastructure.Services.Utilities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisPostgresCollection))]
public sealed class SearchAndRolePrivacyTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [SkippableFact]
    public async Task SearchUsers_ExcludesDisabledAndOptOut()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        _ = redis;

        await using var db = postgres.CreateContext();
        var tsid = new TsidGeneratorService();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var visible = new ApplicationUser
        {
            Id = tsid.GenerateTsid(),
            UserName = $"vis-{suffix}",
            NormalizedUserName = $"VIS-{suffix}".ToUpperInvariant(),
            Email = $"vis-{suffix}@ex.com",
            NormalizedEmail = $"VIS-{suffix}@EX.COM",
            AllowBeSearched = true,
            LockoutEnabled = true,
        };
        var disabled = new ApplicationUser
        {
            Id = tsid.GenerateTsid(),
            UserName = $"dis-{suffix}",
            NormalizedUserName = $"DIS-{suffix}".ToUpperInvariant(),
            Email = $"dis-{suffix}@ex.com",
            NormalizedEmail = $"DIS-{suffix}@EX.COM",
            AllowBeSearched = true,
            LockoutEnabled = true,
            LockoutEnd = DateTimeOffset.MaxValue,
        };
        var hidden = new ApplicationUser
        {
            Id = tsid.GenerateTsid(),
            UserName = $"hid-{suffix}",
            NormalizedUserName = $"HID-{suffix}".ToUpperInvariant(),
            Email = $"hid-{suffix}@ex.com",
            NormalizedEmail = $"HID-{suffix}@EX.COM",
            AllowBeSearched = false,
            LockoutEnabled = true,
        };
        db.Users.AddRange(visible, disabled, hidden);
        await db.SaveChangesAsync();

        var repo = new UserRepository(db, tsid);
        var page = await repo.SearchUsersAsync(suffix, null, 20);
        Assert.Contains(page.Items, u => u.Id == visible.Id);
        Assert.DoesNotContain(page.Items, u => u.Id == disabled.Id);
        Assert.DoesNotContain(page.Items, u => u.Id == hidden.Id);

        Assert.Null(await repo.FindByNameAsync($"dis-{suffix}"));
        Assert.Null(await repo.FindByNameAsync($"hid-{suffix}"));
        Assert.NotNull(await repo.FindByNameAsync($"vis-{suffix}"));
    }

    [SkippableFact]
    public async Task MutateRole_BlocksLastAdmin_AndWritesAuditInTransaction()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);

        await using var db = postgres.CreateContext();
        var tsid = new TsidGeneratorService();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var adminRole = await db.Roles.FirstOrDefaultAsync(r => r.NormalizedName == KnownRoles.Admin.ToUpperInvariant());
        if (adminRole is null)
        {
            adminRole = new ApplicationRoles
            {
                Id = tsid.GenerateTsid(),
                Name = KnownRoles.Admin,
                NormalizedName = KnownRoles.Admin.ToUpperInvariant(),
            };
            db.Roles.Add(adminRole);
            await db.SaveChangesAsync();
        }

        // 共享测试库可能残留 Admin 绑定；本用例需要干净基线。
        var stale = await db.UserRoles.Where(ur => ur.RoleId == adminRole.Id).ToListAsync();
        if (stale.Count > 0)
        {
            db.UserRoles.RemoveRange(stale);
            await db.SaveChangesAsync();
        }

        var admin = new ApplicationUser
        {
            Id = tsid.GenerateTsid(),
            UserName = $"adm-{suffix}",
            NormalizedUserName = $"ADM-{suffix}".ToUpperInvariant(),
            Email = $"adm-{suffix}@ex.com",
            NormalizedEmail = $"ADM-{suffix}@EX.COM",
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        db.Users.Add(admin);
        db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = adminRole.Id });
        await db.SaveChangesAsync();

        var repo = new UserRepository(db, tsid);
        var last = await repo.MutateRoleAsync(
            admin.Id, KnownRoles.Admin, assign: false, admin.Id, "test", null);
        Assert.Equal(RoleMutationOutcome.LastAdmin, last);

        var other = new ApplicationUser
        {
            Id = tsid.GenerateTsid(),
            UserName = $"adm2-{suffix}",
            NormalizedUserName = $"ADM2-{suffix}".ToUpperInvariant(),
            Email = $"adm2-{suffix}@ex.com",
            NormalizedEmail = $"ADM2-{suffix}@EX.COM",
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        db.Users.Add(other);
        db.UserRoles.Add(new UserRole { UserId = other.Id, RoleId = adminRole.Id });
        await db.SaveChangesAsync();

        var ok = await repo.MutateRoleAsync(
            admin.Id, KnownRoles.Admin, assign: false, other.Id, "demote", "127.0.0.1");
        Assert.Equal(RoleMutationOutcome.Success, ok);
        Assert.Equal(1, await repo.CountUsersInRoleAsync(KnownRoles.Admin));
        Assert.True(await db.AdminAuditLogs.AnyAsync(a => a.TargetUserId == admin.Id && a.Action == "RemoveRole"));
        Assert.True(await db.SecurityEvents.AnyAsync(e =>
            e.UserId == admin.Id && e.EventType == Core.Models.Security.SecurityEventType.RoleRemoved));
    }
}
