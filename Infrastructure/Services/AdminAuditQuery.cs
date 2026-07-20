using Core.Interfaces;
using Core.Models.Common;
using Core.Models.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public sealed class AdminAuditQuery(UserDbContext db) : IAdminAuditQuery
{
    public async Task<CursorPage<AdminAuditLogDto>> QueryAsync(
        long? adminUserId,
        long? targetUserId,
        string? action,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(limit, 1, 100);
        long? cursorId = long.TryParse(cursor, out var c) ? c : null;

        var query = db.AdminAuditLogs.AsNoTracking().AsQueryable();
        if (adminUserId is { } a) query = query.Where(x => x.AdminUserId == a);
        if (targetUserId is { } t) query = query.Where(x => x.TargetUserId == t);
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Action == action);
        if (from is { } f) query = query.Where(x => x.CreatedAt >= f);
        if (to is { } t2) query = query.Where(x => x.CreatedAt <= t2);
        if (cursorId.HasValue) query = query.Where(x => x.Id < cursorId.Value);

        var rows = await query
            .OrderByDescending(x => x.Id)
            .Take(pageSize + 1)
            .Select(x => new AdminAuditLogDto
            {
                Id = x.Id,
                AdminUserId = x.AdminUserId,
                TargetUserId = x.TargetUserId,
                Action = x.Action,
                Reason = x.Reason,
                Detail = x.Detail,
                ClientIp = x.ClientIp,
                CreatedAt = x.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        return new CursorPage<AdminAuditLogDto>
        {
            Items = rows,
            HasMore = hasMore,
            NextCursor = hasMore && rows.Count > 0 ? rows[^1].Id.ToString() : null,
        };
    }
}
