using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatApp.Server.IntegrationTests.Support;
using Core.Models.Identity;
using Core.Models.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Http;

[Collection(nameof(RedisPostgresCollection))]
public sealed class WebPipelineTests(PostgresTestFixture postgres, RedisTestFixture redis)
{
    [SkippableFact]
    public async Task RoleRevoke_InvalidatesExistingAccessToken()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var factory = CreateFactory();
        using var client = factory.CreateClientWithDevice($"dev-role-{Guid.NewGuid():N}"[..24]);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var db = postgres.CreateContext())
        {
            // 清理残留 Admin，保证可撤销
            var adminRole = await db.Roles.FirstOrDefaultAsync(r => r.NormalizedName == "ADMIN");
            if (adminRole is not null)
            {
                var links = await db.UserRoles.Where(ur => ur.RoleId == adminRole.Id).ToListAsync();
                db.UserRoles.RemoveRange(links);
                await db.SaveChangesAsync();
            }

            var actor = await WafTestHelpers.SeedUserAsync(db, $"actor-{suffix}", $"actor-{suffix}@ex.com", "Passw0rd!");
            var target = await WafTestHelpers.SeedUserAsync(db, $"tgt-{suffix}", $"tgt-{suffix}@ex.com", "Passw0rd!");
            await WafTestHelpers.AssignRoleAsync(db, actor.Id, KnownRoles.Admin);
            await WafTestHelpers.AssignRoleAsync(db, target.Id, KnownRoles.Admin);
            await WafTestHelpers.AssignRoleAsync(db, target.Id, KnownRoles.User);
        }

        var targetLogin = await WafTestHelpers.LoginAsync(client, $"tgt-{suffix}", "Passw0rd!");
        client.UseBearer(targetLogin.AccessToken!);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users/me")).StatusCode);

        using var adminClient = factory.CreateClientWithDevice($"dev-admin-{Guid.NewGuid():N}"[..24]);
        var adminLogin = await WafTestHelpers.LoginAsync(adminClient, $"actor-{suffix}", "Passw0rd!");
        adminClient.UseBearer(adminLogin.AccessToken!);

        var revoke = await adminClient.SendAsync(new HttpRequestMessage(
            HttpMethod.Delete, $"/api/users/{targetLogin.UserId}/roles/{KnownRoles.Admin}")
        {
            Content = JsonContent.Create(new { reason = "waf", confirmSelfDemotion = false }, options: WafTestHelpers.Json),
        });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        // 旧 access token 应立即失效（会话已全部撤销）
        var me = await client.GetAsync("/api/users/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [SkippableFact]
    public async Task SecurityVersionAdvance_RejectsCachedAccessTokenWithoutRedisRevocation()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var factory = CreateFactory();
        using var client = factory.CreateClientWithDevice($"dev-version-{Guid.NewGuid():N}"[..24]);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        long userId;
        await using (var db = postgres.CreateContext())
        {
            var user = await WafTestHelpers.SeedUserAsync(
                db, $"version-{suffix}", $"version-{suffix}@ex.com", "Passw0rd!");
            userId = user.Id;
        }

        var login = await WafTestHelpers.LoginAsync(client, $"version-{suffix}", "Passw0rd!");
        client.UseBearer(login.AccessToken!);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users/me")).StatusCode);

        // Deliberately change only PostgreSQL. The access token remains in Redis
        // and in the in-process L1 cache, reproducing a failed revoke dual-write.
        await using (var db = postgres.CreateContext())
        {
            await db.Users
                .Where(user => user.Id == userId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(user => user.SecurityVersion, user => user.SecurityVersion + 1)
                    .SetProperty(user => user.SecurityStamp, Guid.NewGuid().ToString()));
        }

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/users/me")).StatusCode);
    }

    [SkippableFact]
    public async Task Idempotent_FriendRequest_Concurrent_OnlyOneCreatesPending()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var sharedPrefix = $"waf-idem:{Guid.NewGuid():N}:";
        var avatarRoot = Path.Combine(Path.GetTempPath(), "chatapp-waf-avatars", Guid.NewGuid().ToString("N"));
        await using var factoryA = CreateFactory(sharedPrefix, avatarRoot);
        await using var factoryB = CreateFactory(sharedPrefix, avatarRoot);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        long requesterId, targetId, otherTargetId;
        await using (var db = postgres.CreateContext())
        {
            var requester = await WafTestHelpers.SeedUserAsync(db, $"req-{suffix}", $"req-{suffix}@ex.com", "Passw0rd!");
            var target = await WafTestHelpers.SeedUserAsync(db, $"tar-{suffix}", $"tar-{suffix}@ex.com", "Passw0rd!");
            var otherTarget = await WafTestHelpers.SeedUserAsync(
                db, $"tar2-{suffix}", $"tar2-{suffix}@ex.com", "Passw0rd!");
            requesterId = requester.Id;
            targetId = target.Id;
            otherTargetId = otherTarget.Id;
        }

        using var clientA = factoryA.CreateClientWithDevice($"dev-idem-a-{Guid.NewGuid():N}"[..28]);
        using var clientB = factoryB.CreateClientWithDevice($"dev-idem-b-{Guid.NewGuid():N}"[..28]);
        // 同一用户、同一设备指纹前缀会导致不同会话；幂等按 userId，需同一用户 token。
        // 用同一客户端登录拿 token，再复制到两个实例的 HttpClient。
        var login = await WafTestHelpers.LoginAsync(clientA, $"req-{suffix}", "Passw0rd!");
        clientA.UseBearer(login.AccessToken!);
        clientB.UseBearer(login.AccessToken!);

        var idemKey = $"idem-{Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(new { targetUserId = targetId, message = "hi" }, WafTestHelpers.Json);

        async Task<HttpResponseMessage> SendAsync(HttpClient c)
        {
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/Friendship/requests") { Content = content };
            req.Headers.Add("X-Idempotency-Key", idemKey);
            return await c.SendAsync(req);
        }

        var tasks = new[] { SendAsync(clientA), SendAsync(clientB) };
        var results = await Task.WhenAll(tasks);
        var bodies = await Task.WhenAll(results.Select(async r =>
            $"{(int)r.StatusCode} replay={r.Headers.Contains("X-Idempotent-Replay")} body={await r.Content.ReadAsStringAsync()}"));

        // 若并发均落到 processing Conflict，短暂等待后由任一实例重放完成态。
        if (results.All(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            await Task.Delay(200);
            var retry = await SendAsync(clientA);
            results = [.. results, retry];
            bodies = [.. bodies, $"{(int)retry.StatusCode} replay={retry.Headers.Contains("X-Idempotent-Replay")} body={await retry.Content.ReadAsStringAsync()}"];
        }

        var okOrReplay = results.Count(r =>
            r.StatusCode == HttpStatusCode.OK
            || r.Headers.Contains("X-Idempotent-Replay"));
        Assert.True(okOrReplay >= 1, string.Join(" | ", bodies));
        Assert.True(results.Count(r => r.StatusCode == HttpStatusCode.OK && !r.Headers.Contains("X-Idempotent-Replay")) <= 1,
            string.Join(" | ", bodies));

        await using var check = postgres.CreateContext();
        var pending = await check.FriendRequests.CountAsync(r =>
            r.RequesterId == requesterId && r.TargetUserId == targetId);
        Assert.True(pending == 1, $"pending={pending}; {string.Join(" | ", bodies)}");

        var differentPayload = JsonSerializer.Serialize(
            new { targetUserId = otherTargetId, message = "different" }, WafTestHelpers.Json);
        using var differentRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Friendship/requests")
        {
            Content = new StringContent(differentPayload, Encoding.UTF8, "application/json"),
        };
        differentRequest.Headers.Add("X-Idempotency-Key", idemKey);
        var different = await clientA.SendAsync(differentRequest);
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        Assert.Contains(
            "不同请求体",
            await different.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.False(await check.FriendRequests.AnyAsync(r =>
            r.RequesterId == requesterId && r.TargetUserId == otherTargetId));
    }

    [SkippableFact]
    public async Task Avatar_Ticket_CrossInstance_Expired_ForgedType_OversizedPixels()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var sharedPrefix = $"waf-av:{Guid.NewGuid():N}:";
        var avatarRoot = Path.Combine(Path.GetTempPath(), "chatapp-waf-avatars", Guid.NewGuid().ToString("N"));
        await using var factoryA = CreateFactory(sharedPrefix, avatarRoot);
        await using var factoryB = CreateFactory(sharedPrefix, avatarRoot);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var db = postgres.CreateContext())
            await WafTestHelpers.SeedUserAsync(db, $"av-{suffix}", $"av-{suffix}@ex.com", "Passw0rd!");

        using var clientA = factoryA.CreateClientWithDevice($"dev-av-a-{Guid.NewGuid():N}"[..24]);
        using var clientB = factoryB.CreateClientWithDevice($"dev-av-b-{Guid.NewGuid():N}"[..24]);
        var login = await WafTestHelpers.LoginAsync(clientA, $"av-{suffix}", "Passw0rd!");
        clientA.UseBearer(login.AccessToken!);
        clientB.UseBearer(login.AccessToken!);

        // 跨实例：A 开票，B 上传
        var jpeg = WafTestHelpers.CreateJpeg(64, 64);
        var presign = await clientA.PostAsJsonAsync("/api/users/me/avatar/presign",
            new { contentType = "image/jpeg", contentLength = jpeg.Length }, WafTestHelpers.Json);
        if (presign.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"presign {(int)presign.StatusCode}: {await presign.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.OK, presign.StatusCode);
        var ticketBody = await presign.Content.ReadFromJsonAsync<AvatarTicketDto>(WafTestHelpers.Json);
        Assert.False(string.IsNullOrWhiteSpace(ticketBody?.Ticket));

        using (var content = new ByteArrayContent(jpeg))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            var upload = await clientB.PutAsync(
                $"/api/users/me/avatar/upload?ticket={Uri.EscapeDataString(ticketBody!.Ticket)}", content);
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        }

        // 伪造 Content-Type
        var presign2 = await clientA.PostAsJsonAsync("/api/users/me/avatar/presign",
            new { contentType = "image/jpeg", contentLength = jpeg.Length }, WafTestHelpers.Json);
        var ticket2 = (await presign2.Content.ReadFromJsonAsync<AvatarTicketDto>(WafTestHelpers.Json))!.Ticket;
        using (var content = new ByteArrayContent(jpeg))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            var forged = await clientA.PutAsync(
                $"/api/users/me/avatar/upload?ticket={Uri.EscapeDataString(ticket2)}", content);
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        }

        // 过期：通过同前缀值存储删除票据
        var presign3 = await clientA.PostAsJsonAsync("/api/users/me/avatar/presign",
            new { contentType = "image/jpeg", contentLength = jpeg.Length }, WafTestHelpers.Json);
        var ticket3 = (await presign3.Content.ReadFromJsonAsync<AvatarTicketDto>(WafTestHelpers.Json))!.Ticket;
        using (var scope = factoryA.Services.CreateScope())
        {
            var cache = scope.ServiceProvider.GetRequiredService<Core.Interfaces.Cache.ICacheValueStore>();
            await cache.RemoveAsync($"avatar:ticket:{ticket3}");
        }

        using (var content = new ByteArrayContent(jpeg))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            var expired = await clientA.PutAsync(
                $"/api/users/me/avatar/upload?ticket={Uri.EscapeDataString(ticket3)}", content);
            Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        }

        // 超大像素
        var huge = WafTestHelpers.CreateJpeg(2200, 2200);
        var presign4 = await clientA.PostAsJsonAsync("/api/users/me/avatar/presign",
            new { contentType = "image/jpeg", contentLength = huge.Length }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, presign4.StatusCode);
        var ticket4 = (await presign4.Content.ReadFromJsonAsync<AvatarTicketDto>(WafTestHelpers.Json))!.Ticket;
        using (var content = new ByteArrayContent(huge))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            var oversized = await clientA.PutAsync(
                $"/api/users/me/avatar/upload?ticket={Uri.EscapeDataString(ticket4)}", content);
            Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Avatar_Ticket_ConcurrentUpload_OnlyOneSucceeds()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var sharedPrefix = $"waf-av-race:{Guid.NewGuid():N}:";
        var avatarRoot = Path.Combine(Path.GetTempPath(), "chatapp-waf-avatars", Guid.NewGuid().ToString("N"));
        await using var factory = CreateFactory(sharedPrefix, avatarRoot);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var db = postgres.CreateContext())
            await WafTestHelpers.SeedUserAsync(db, $"avr-{suffix}", $"avr-{suffix}@ex.com", "Passw0rd!");

        using var client = factory.CreateClientWithDevice($"dev-avr-{Guid.NewGuid():N}"[..24]);
        var login = await WafTestHelpers.LoginAsync(client, $"avr-{suffix}", "Passw0rd!");
        client.UseBearer(login.AccessToken!);

        var jpeg = WafTestHelpers.CreateJpeg(64, 64);
        var presign = await client.PostAsJsonAsync("/api/users/me/avatar/presign",
            new { contentType = "image/jpeg", contentLength = jpeg.Length }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.OK, presign.StatusCode);
        var ticket = (await presign.Content.ReadFromJsonAsync<AvatarTicketDto>(WafTestHelpers.Json))!.Ticket;

        using var c1 = new ByteArrayContent(jpeg);
        c1.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var c2 = new ByteArrayContent(jpeg);
        c2.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var url = $"/api/users/me/avatar/upload?ticket={Uri.EscapeDataString(ticket)}";

        var t1 = client.PutAsync(url, c1);
        var t2 = client.PutAsync(url, c2);
        await Task.WhenAll(t1, t2);

        var statuses = new[] { t1.Result.StatusCode, t2.Result.StatusCode };
        Assert.Contains(HttpStatusCode.OK, statuses);
        Assert.Contains(HttpStatusCode.BadRequest, statuses);
    }

    [SkippableFact]
    public async Task Search_ExcludesDisabledUsers()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var factory = CreateFactory();
        using var client = factory.CreateClientWithDevice($"dev-search-{Guid.NewGuid():N}"[..24]);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var term = $"wafsrch{suffix}";
        await using (var db = postgres.CreateContext())
        {
            await WafTestHelpers.SeedUserAsync(db, $"{term}ok", $"{term}ok@ex.com", "Passw0rd!");
            var disabled = await WafTestHelpers.SeedUserAsync(db, $"{term}bad", $"{term}bad@ex.com", "Passw0rd!");
            disabled.LockoutEnabled = true;
            disabled.LockoutEnd = DateTimeOffset.MaxValue;
            await db.SaveChangesAsync();

            var viewer = await WafTestHelpers.SeedUserAsync(db, $"viewer-{suffix}", $"viewer-{suffix}@ex.com", "Passw0rd!");
            _ = viewer;
        }

        var login = await WafTestHelpers.LoginAsync(client, $"viewer-{suffix}", "Passw0rd!");
        client.UseBearer(login.AccessToken!);

        var res = await client.GetAsync($"/api/users/search?q={term}&limit=20");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadAsStringAsync();
        Assert.Contains($"{term}ok", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"{term}bad", json, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task CorrelationId_And_ProblemDetails_And_RateLimit()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var factory = CreateFactory(extra: new Dictionary<string, string?>
        {
            ["RateLimiting:AuthLoginPermitLimit"] = "2",
            ["RateLimiting:AuthLoginWindowSeconds"] = "60",
        });
        using var client = factory.CreateClientWithDevice($"dev-pipe-{Guid.NewGuid():N}"[..24]);

        var cid = $"cid-{Guid.NewGuid():N}";
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/__test/problem");
        req.Headers.Add("X-Correlation-Id", cid);
        var problem = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, problem.StatusCode);
        Assert.Equal(cid, problem.Headers.GetValues("X-Correlation-Id").FirstOrDefault());
        Assert.Equal("application/problem+json", problem.Content.Headers.ContentType?.MediaType);
        var body = await problem.Content.ReadAsStringAsync();
        Assert.Contains("intentional test probe", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bad_request", body, StringComparison.OrdinalIgnoreCase);

        // 限流：匿名登录连续打满
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var db = postgres.CreateContext())
            await WafTestHelpers.SeedUserAsync(db, $"rl-{suffix}", $"rl-{suffix}@ex.com", "Passw0rd!");

        HttpStatusCode last = 0;
        for (var i = 0; i < 4; i++)
        {
            var loginRes = await client.PostAsJsonAsync("/api/auth/login",
                new { username = $"rl-{suffix}", password = "Passw0rd!" }, WafTestHelpers.Json);
            last = loginRes.StatusCode;
            if (last == HttpStatusCode.TooManyRequests)
                break;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    [SkippableFact]
    public async Task RateLimit_SharedAcrossTwoAppInstances()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        var sharedPrefix = $"waf-rl:{Guid.NewGuid():N}:";
        var extra = new Dictionary<string, string?>
        {
            ["RateLimiting:AuthLoginPermitLimit"] = "3",
            ["RateLimiting:AuthLoginWindowSeconds"] = "60",
        };
        await using var factoryA = CreateFactory(sharedPrefix, extra: extra);
        await using var factoryB = CreateFactory(sharedPrefix, extra: extra);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var db = postgres.CreateContext())
            await WafTestHelpers.SeedUserAsync(db, $"rl2-{suffix}", $"rl2-{suffix}@ex.com", "Passw0rd!");

        using var clientA = factoryA.CreateClientWithDevice($"dev-rl-a-{Guid.NewGuid():N}"[..24]);
        using var clientB = factoryB.CreateClientWithDevice($"dev-rl-b-{Guid.NewGuid():N}"[..24]);

        // 两实例合计消耗同一 Redis 窗口额度
        Assert.NotEqual(HttpStatusCode.TooManyRequests,
            (await clientA.PostAsJsonAsync("/api/auth/login",
                new { username = $"rl2-{suffix}", password = "Passw0rd!" }, WafTestHelpers.Json)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests,
            (await clientB.PostAsJsonAsync("/api/auth/login",
                new { username = $"rl2-{suffix}", password = "Passw0rd!" }, WafTestHelpers.Json)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests,
            (await clientA.PostAsJsonAsync("/api/auth/login",
                new { username = $"rl2-{suffix}", password = "Passw0rd!" }, WafTestHelpers.Json)).StatusCode);

        var blocked = await clientB.PostAsJsonAsync("/api/auth/login",
            new { username = $"rl2-{suffix}", password = "Passw0rd!" }, WafTestHelpers.Json);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }

    [SkippableFact]
    public async Task AdminRoleMutation_WritesAuditAndSecurityEvent_AtomicallyOnSuccess()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var factory = CreateFactory();
        using var client = factory.CreateClientWithDevice($"dev-audit-{Guid.NewGuid():N}"[..24]);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        long actorId, targetId;
        await using (var db = postgres.CreateContext())
        {
            var adminRole = await db.Roles.FirstOrDefaultAsync(r => r.NormalizedName == "ADMIN");
            if (adminRole is not null)
            {
                db.UserRoles.RemoveRange(await db.UserRoles.Where(ur => ur.RoleId == adminRole.Id).ToListAsync());
                await db.SaveChangesAsync();
            }

            var actor = await WafTestHelpers.SeedUserAsync(db, $"aa-{suffix}", $"aa-{suffix}@ex.com", "Passw0rd!");
            var target = await WafTestHelpers.SeedUserAsync(db, $"at-{suffix}", $"at-{suffix}@ex.com", "Passw0rd!");
            await WafTestHelpers.AssignRoleAsync(db, actor.Id, KnownRoles.Admin);
            await WafTestHelpers.AssignRoleAsync(db, target.Id, KnownRoles.User);
            actorId = actor.Id;
            targetId = target.Id;
        }

        var login = await WafTestHelpers.LoginAsync(client, $"aa-{suffix}", "Passw0rd!");
        client.UseBearer(login.AccessToken!);

        var assign = await client.PostAsJsonAsync($"/api/users/{targetId}/roles",
            new { roleName = KnownRoles.Admin, reason = "waf-audit" }, WafTestHelpers.Json);
        if (assign.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"assign {(int)assign.StatusCode}: {await assign.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.OK, assign.StatusCode);

        await using var check = postgres.CreateContext();
        Assert.True(await check.AdminAuditLogs.AnyAsync(a =>
            a.AdminUserId == actorId && a.TargetUserId == targetId && a.Action == "AssignRole"));
        Assert.True(await check.SecurityEvents.AnyAsync(e =>
            e.UserId == targetId && e.EventType == SecurityEventType.RoleAssigned));

        // 先撤掉 target 的 Admin，使 actor 成为唯一管理员
        var demoteTarget = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Delete, $"/api/users/{targetId}/roles/{KnownRoles.Admin}")
        {
            Content = JsonContent.Create(new { reason = "prep", confirmSelfDemotion = false }, options: WafTestHelpers.Json),
        });
        Assert.Equal(HttpStatusCode.OK, demoteTarget.StatusCode);

        // 失败路径：撤销最后一个 Admin → 无针对 actor 的 RemoveRole 审计增量
        // 需重新登录：角色变更会撤销全部会话
        var login2 = await WafTestHelpers.LoginAsync(client, $"aa-{suffix}", "Passw0rd!");
        client.UseBearer(login2.AccessToken!);

        var before = await check.AdminAuditLogs.CountAsync(a => a.Action == "RemoveRole" && a.TargetUserId == actorId);
        var lastAdmin = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Delete, $"/api/users/{actorId}/roles/{KnownRoles.Admin}")
        {
            Content = JsonContent.Create(new { reason = "self", confirmSelfDemotion = true }, options: WafTestHelpers.Json),
        });
        Assert.Equal(HttpStatusCode.BadRequest, lastAdmin.StatusCode);
        var after = await check.AdminAuditLogs.CountAsync(a => a.Action == "RemoveRole" && a.TargetUserId == actorId);
        Assert.Equal(before, after);
    }

    [SkippableFact]
    public async Task RequestBody_OverKestrelLimit_IsRejected()
    {
        Skip.If(!postgres.IsAvailable, postgres.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);
        Skip.If(!redis.IsAvailable, redis.SkipReason);

        await using var factory = CreateFactory();
        using var client = factory.CreateClientWithDevice($"dev-size-{Guid.NewGuid():N}"[..24]);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var db = postgres.CreateContext())
            await WafTestHelpers.SeedUserAsync(db, $"sz-{suffix}", $"sz-{suffix}@ex.com", "Passw0rd!");

        var login = await WafTestHelpers.LoginAsync(client, $"sz-{suffix}", "Passw0rd!");
        client.UseBearer(login.AccessToken!);

        // Kestrel MaxRequestBodySize = 3MB；中间件按 Content-Length 拒绝，必须 413/400
        var oversized = new byte[3 * 1024 * 1024 + 1024];
        Random.Shared.NextBytes(oversized);
        using var content = new ByteArrayContent(oversized);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = oversized.Length;
        var res = await client.PutAsync("/api/users/me/avatar/upload?ticket=deadbeef", content);
        Assert.True(
            res.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest,
            $"unexpected status {res.StatusCode}");
    }

    private ChatAppWebApplicationFactory CreateFactory(
        string? keyPrefix = null,
        string? avatarRoot = null,
        IReadOnlyDictionary<string, string?>? extra = null)
        => new(
            postgres.ConnectionString,
            redis.ConnectionString,
            keyPrefix,
            avatarRoot,
            extra);

    private sealed class AvatarTicketDto
    {
        public string Ticket { get; set; } = "";
        public string ObjectKey { get; set; } = "";
    }
}
