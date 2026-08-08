using System.Data;
using System.Data.Common;
using ChatApp.Realtime.Abstractions.Events;
using ChatApp.Realtime.Abstractions.Stores;
using ChatApp.Realtime.Integration.Outbox;
using Core.Interfaces;
using Core.Models.Export;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Services;

public sealed class RealtimeOutboxAdminService(UserDbContext db) : IRealtimeOutboxAdminService
{
    public async Task<RealtimeOutboxSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pending = (short)RealtimeOutboxStatus.Pending;
        var dead = (short)RealtimeOutboxStatus.Dead;
        var published = (short)RealtimeOutboxStatus.Published;

        var rows = await db.RealtimeOutbox.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                PendingCount = g.Count(x => x.Status == pending),
                DeadCount = g.Count(x => x.Status == dead),
                PublishedCount = g.Count(x => x.Status == published),
                OldestPendingAtMs = g.Where(x => x.Status == pending).Min(x => (long?)x.CreatedAtMs),
                OldestDeadAtMs = g.Where(x => x.Status == dead).Min(x => (long?)x.CreatedAtMs),
                MaxPendingAttemptCount = g.Where(x => x.Status == pending).Max(x => (int?)x.AttemptCount) ?? 0,
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows is null)
        {
            return new RealtimeOutboxSummaryDto(0, 0, 0, null, null, null, 0, now);
        }

        long? oldestAge = rows.OldestPendingAtMs is { } oldest
            ? Math.Max(0, now - oldest)
            : null;

        return new RealtimeOutboxSummaryDto(
            rows.PendingCount,
            rows.DeadCount,
            rows.PublishedCount,
            rows.OldestPendingAtMs,
            oldestAge,
            rows.OldestDeadAtMs,
            rows.MaxPendingAttemptCount,
            now);
    }

    public async Task<RealtimeOutboxListResponse> ListAsync(
        string? status = null,
        long? targetUserId = null,
        short? eventType = null,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 200);

        var query = db.RealtimeOutbox.AsNoTracking().AsQueryable();
        short? statusFilter = null;
        if (TryParseStatus(status, out var statusValue))
        {
            statusFilter = statusValue;
            query = query.Where(x => x.Status == statusValue);
        }
        if (targetUserId is > 0)
            query = query.Where(x => x.TargetUserId == targetUserId.Value);
        if (eventType is not null)
            query = query.Where(x => x.EventType == eventType.Value);

        // Dead / Pending 看创建时间；Published 看发布时间。
        query = statusFilter == (short)RealtimeOutboxStatus.Published
            ? query.OrderByDescending(x => x.PublishedAtMs).ThenByDescending(x => x.CreatedAtMs)
            : query.OrderByDescending(x => x.CreatedAtMs);

        var items = await query
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new RealtimeOutboxListResponse(
            items.Select(item => ToDto(item)).ToList(),
            offset,
            limit,
            items.Count);
    }

    public async Task<RealtimeOutboxItemDto?> GetAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            return null;

        var item = await db.RealtimeOutbox.AsNoTracking()
            .FirstOrDefaultAsync(x => x.EventId == eventId.Trim(), cancellationToken)
            .ConfigureAwait(false);
        return item is null ? null : ToDto(item, includeFullPreview: true);
    }

    public async Task<(bool Ok, string? Error)> ReplayDeadAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 64)
            return (false, "invalid_event_id");

        var id = eventId.Trim();
        var existing = await db.RealtimeOutbox.AsNoTracking()
            .Where(x => x.EventId == id)
            .Select(x => new { x.Status })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
            return (false, "not_found");
        if (existing.Status == (short)RealtimeOutboxStatus.Published)
            return (false, "already_published");
        if (existing.Status != (short)RealtimeOutboxStatus.Dead)
            return (false, "not_dead");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var updated = await db.RealtimeOutbox
            .Where(x => x.EventId == id && x.Status == (short)RealtimeOutboxStatus.Dead)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status, (short)RealtimeOutboxStatus.Pending)
                    .SetProperty(x => x.PublishedAtMs, (long?)null)
                    .SetProperty(x => x.AttemptCount, 0)
                    .SetProperty(x => x.NextAttemptAtMs, now)
                    .SetProperty(x => x.LockedBy, (string?)null)
                    .SetProperty(x => x.LockedUntilMs, (long?)null)
                    .SetProperty(x => x.LastError, (string?)null),
                cancellationToken)
            .ConfigureAwait(false);

        return updated == 0 ? (false, "not_dead") : (true, null);
    }

    public async Task<RealtimeOutboxBatchReplayResult> ReplayDeadBatchAsync(
        IReadOnlyList<string> eventIds,
        CancellationToken cancellationToken = default)
    {
        var ids = eventIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Where(x => x.Length <= 64)
            .Distinct(StringComparer.Ordinal)
            .Take(100)
            .ToArray();

        if (ids.Length == 0)
            return new RealtimeOutboxBatchReplayResult(0, 0, []);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dead = (short)RealtimeOutboxStatus.Dead;
        var pending = (short)RealtimeOutboxStatus.Pending;

        var replayedIds = new HashSet<string>(StringComparer.Ordinal);
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                UPDATE realtime.outbox
                SET status = @pending,
                    published_at_ms = NULL,
                    attempt_count = 0,
                    next_attempt_at_ms = @now,
                    locked_by = NULL,
                    locked_until_ms = NULL,
                    last_error = NULL
                WHERE event_id = ANY(@ids)
                  AND status = @dead
                RETURNING event_id;
                """;

            AddParam(cmd, "pending", pending);
            AddParam(cmd, "now", now);
            AddParam(cmd, "dead", dead);
            var idsParam = cmd.CreateParameter();
            idsParam.ParameterName = "ids";
            if (idsParam is NpgsqlParameter npgsqlIds)
            {
                npgsqlIds.Value = ids;
                npgsqlIds.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text;
            }
            else
            {
                idsParam.Value = ids;
            }

            cmd.Parameters.Add(idsParam);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                replayedIds.Add(reader.GetString(0));
        }

        var skipped = new List<string>();
        var remaining = ids.Where(id => !replayedIds.Contains(id)).ToList();
        if (remaining.Count > 0)
        {
            var statuses = await db.RealtimeOutbox.AsNoTracking()
                .Where(x => remaining.Contains(x.EventId))
                .Select(x => new { x.EventId, x.Status })
                .ToDictionaryAsync(x => x.EventId, x => x.Status, StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);

            foreach (var id in remaining)
            {
                if (!statuses.TryGetValue(id, out var status))
                    skipped.Add($"{id}:not_found");
                else if (status == (short)RealtimeOutboxStatus.Published)
                    skipped.Add($"{id}:already_published");
                else
                    skipped.Add($"{id}:not_dead");
            }
        }

        return new RealtimeOutboxBatchReplayResult(ids.Length, replayedIds.Count, skipped);
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static bool TryParseStatus(string? status, out short value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(status))
            return false;

        if (short.TryParse(status, out var numeric)
            && Enum.IsDefined(typeof(RealtimeOutboxStatus), numeric))
        {
            value = numeric;
            return true;
        }

        if (Enum.TryParse<RealtimeOutboxStatus>(status, ignoreCase: true, out var named))
        {
            value = (short)named;
            return true;
        }

        return false;
    }

    private static RealtimeOutboxItemDto ToDto(
        RealtimeIntegrationOutboxItem item,
        bool includeFullPreview = false)
    {
        var preview = item.PayloadJson;
        if (!includeFullPreview && preview.Length > 240)
            preview = preview[..240] + "…";

        var statusName = Enum.IsDefined(typeof(RealtimeOutboxStatus), item.Status)
            ? ((RealtimeOutboxStatus)item.Status).ToString()
            : item.Status.ToString();

        var typeName = Enum.IsDefined(typeof(RealtimeEventType), (byte)item.EventType)
            ? ((RealtimeEventType)item.EventType).ToString()
            : item.EventType.ToString();

        return new RealtimeOutboxItemDto(
            item.EventId,
            item.Status,
            statusName,
            item.EventType,
            typeName,
            item.TargetUserId,
            item.AttemptCount,
            item.CreatedAtMs,
            item.NextAttemptAtMs,
            item.PublishedAtMs,
            item.LockedBy,
            item.LockedUntilMs,
            item.LastError,
            preview);
    }
}
