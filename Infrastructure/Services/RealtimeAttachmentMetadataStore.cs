using Core.Interfaces;
using Core.Models.Attachment;
using Core.Models.Export;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.Services;

/// <summary>
/// 直连 Realtime Postgres <c>{schema}.attachments</c>（Migration012 契约）。
/// Realtime 未落地时测试可自行 CREATE TABLE 匹配本 SQL。
/// </summary>
public sealed class RealtimeAttachmentMetadataStore : IAttachmentMetadataStore
{
    private readonly MessageEvidenceOptions _evidence;
    private readonly DataExportStorageOptions _export;
    private readonly ILogger<RealtimeAttachmentMetadataStore> _logger;

    public RealtimeAttachmentMetadataStore(
        IOptions<MessageEvidenceOptions> evidence,
        IOptions<DataExportStorageOptions> export,
        ILogger<RealtimeAttachmentMetadataStore> logger)
    {
        _evidence = evidence.Value;
        _export = export.Value;
        _logger = logger;
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(ResolveConnectionString());

    public string UnavailableReason =>
        IsAvailable
            ? string.Empty
            : "未配置 MessageEvidence:RealtimeConnectionString / DataExport:RealtimeConnectionString";

    public async Task InsertTicketedAsync(
        string attachmentId,
        long uploaderUserId,
        string objectKey,
        string? publicUrl,
        string contentType,
        long sizeBytes,
        string? originalName,
        string? clientAttachmentId = null,
        CancellationToken cancellationToken = default)
    {
        var cs = RequireConnectionString();
        var table = TableSql();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
             INSERT INTO {table} (
                 attachment_id, uploader_user_id, object_key, public_url,
                 content_type, size_bytes, original_name, status,
                 message_id, conversation_id, client_attachment_id,
                 created_at_ms, confirmed_at_ms, bound_at_ms)
             VALUES (
                 @id, @uid, @key, @url,
                 @ct, @size, @name, @status,
                 NULL, NULL, @clientId,
                 @created, NULL, NULL)
             ON CONFLICT (attachment_id) DO NOTHING
             """, conn);

        cmd.Parameters.AddWithValue("id", attachmentId);
        cmd.Parameters.AddWithValue("uid", uploaderUserId);
        cmd.Parameters.AddWithValue("key", objectKey);
        cmd.Parameters.AddWithValue("url", (object?)publicUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ct", contentType);
        cmd.Parameters.AddWithValue("size", sizeBytes);
        cmd.Parameters.AddWithValue("name", (object?)originalName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", (short)AttachmentStatus.Ticketed);
        cmd.Parameters.AddWithValue("clientId", (object?)clientAttachmentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("created", nowMs);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ConfirmAsync(
        string attachmentId,
        long uploaderUserId,
        string objectKey,
        string? publicUrl,
        string contentType,
        long sizeBytes,
        string? originalName = null,
        CancellationToken cancellationToken = default)
    {
        var cs = RequireConnectionString();
        var table = TableSql();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Upsert：Local 可能已 InsertTicketed；若元数据不可用时跳过了 ticketed，则在此插入。
        await using var cmd = new NpgsqlCommand(
            $"""
             INSERT INTO {table} (
                 attachment_id, uploader_user_id, object_key, public_url,
                 content_type, size_bytes, original_name, status,
                 message_id, conversation_id, client_attachment_id,
                 created_at_ms, confirmed_at_ms, bound_at_ms)
             VALUES (
                 @id, @uid, @key, @url,
                 @ct, @size, @name, @statusConfirmed,
                 NULL, NULL, NULL,
                 @created, @confirmedAt, NULL)
             ON CONFLICT (attachment_id) DO UPDATE SET
                 object_key = EXCLUDED.object_key,
                 public_url = EXCLUDED.public_url,
                 content_type = CASE
                     WHEN EXCLUDED.size_bytes > 0 THEN EXCLUDED.content_type
                     ELSE {table}.content_type END,
                 size_bytes = CASE
                     WHEN EXCLUDED.size_bytes > 0 THEN EXCLUDED.size_bytes
                     ELSE {table}.size_bytes END,
                 original_name = COALESCE(EXCLUDED.original_name, {table}.original_name),
                 status = @statusConfirmed,
                 confirmed_at_ms = @confirmedAt
             WHERE {table}.uploader_user_id = @uid
               AND {table}.status IN (@statusTicketed, @statusUploaded, @statusScanning, @statusConfirmed)
             """, conn);

        cmd.Parameters.AddWithValue("id", attachmentId);
        cmd.Parameters.AddWithValue("uid", uploaderUserId);
        cmd.Parameters.AddWithValue("key", objectKey);
        cmd.Parameters.AddWithValue("url", (object?)publicUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ct", contentType);
        cmd.Parameters.AddWithValue("size", sizeBytes);
        cmd.Parameters.AddWithValue("name", (object?)originalName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("statusConfirmed", (short)AttachmentStatus.Confirmed);
        cmd.Parameters.AddWithValue("statusTicketed", (short)AttachmentStatus.Ticketed);
        cmd.Parameters.AddWithValue("statusUploaded", (short)AttachmentStatus.Uploaded);
        cmd.Parameters.AddWithValue("statusScanning", (short)AttachmentStatus.Scanning);
        cmd.Parameters.AddWithValue("confirmedAt", nowMs);
        cmd.Parameters.AddWithValue("created", nowMs);

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
            _logger.LogWarning(
                "附件 Confirm 未更新任何行 AttachmentId={Id} UserId={UserId}",
                attachmentId, uploaderUserId);
    }

    public async Task MarkUploadedScanningAsync(
        string attachmentId,
        long uploaderUserId,
        long sizeBytes,
        string? sha256Hex = null,
        CancellationToken cancellationToken = default)
    {
        var cs = RequireConnectionString();
        var table = TableSql();
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Ticketed → Uploaded → Scanning（单语句落到 Scanning；size / content_hash 有则更新）
        await using var cmd = new NpgsqlCommand(
            $"""
             UPDATE {table}
             SET status = @scanning,
                 size_bytes = CASE WHEN @size > 0 THEN @size ELSE size_bytes END,
                 content_hash = CASE
                     WHEN @hash IS NOT NULL AND length(@hash) > 0 THEN lower(@hash)
                     ELSE content_hash
                 END
             WHERE attachment_id = @id
               AND uploader_user_id = @uid
               AND status IN (@ticketed, @uploaded, @scanning)
             """, conn);
        cmd.Parameters.AddWithValue("id", attachmentId);
        cmd.Parameters.AddWithValue("uid", uploaderUserId);
        cmd.Parameters.AddWithValue("size", sizeBytes);
        cmd.Parameters.AddWithValue(
            "hash",
            string.IsNullOrWhiteSpace(sha256Hex) ? (object)DBNull.Value : sha256Hex.Trim());
        cmd.Parameters.AddWithValue("scanning", (short)AttachmentStatus.Scanning);
        cmd.Parameters.AddWithValue("ticketed", (short)AttachmentStatus.Ticketed);
        cmd.Parameters.AddWithValue("uploaded", (short)AttachmentStatus.Uploaded);

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
            _logger.LogWarning(
                "附件 MarkUploadedScanning 未更新任何行 AttachmentId={Id} UserId={UserId}",
                attachmentId, uploaderUserId);
    }

    public async Task MarkRejectedAsync(
        string attachmentId,
        long uploaderUserId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var cs = RequireConnectionString();
        var table = TableSql();
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(
            $"""
             UPDATE {table}
             SET status = @rejected
             WHERE attachment_id = @id
               AND uploader_user_id = @uid
               AND status IN (@ticketed, @uploaded, @scanning)
             """, conn);
        cmd.Parameters.AddWithValue("id", attachmentId);
        cmd.Parameters.AddWithValue("uid", uploaderUserId);
        cmd.Parameters.AddWithValue("rejected", (short)AttachmentStatus.Rejected);
        cmd.Parameters.AddWithValue("ticketed", (short)AttachmentStatus.Ticketed);
        cmd.Parameters.AddWithValue("uploaded", (short)AttachmentStatus.Uploaded);
        cmd.Parameters.AddWithValue("scanning", (short)AttachmentStatus.Scanning);

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
            _logger.LogWarning(
                "附件 MarkRejected 未更新任何行 AttachmentId={Id} UserId={UserId} Reason={Reason}",
                attachmentId, uploaderUserId, reason);
        else
            _logger.LogInformation(
                "附件已拒绝 AttachmentId={Id} UserId={UserId} Reason={Reason}",
                attachmentId, uploaderUserId, reason);
    }

    public async Task<AttachmentDownloadAccess> ResolveDownloadAccessAsync(
        string attachmentId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var cs = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
        {
            return new AttachmentDownloadAccess(
                attachmentId, string.Empty, "application/octet-stream", null,
                AttachmentDownloadDecision.Unavailable);
        }

        var table = TableSql();
        var members = MembersTableSql();
        var messages = MessagesTableSql();

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var cmd = new NpgsqlCommand(
                $"""
                 SELECT
                     a.attachment_id, a.object_key, a.content_type, a.original_name,
                     a.status, a.uploader_user_id, a.conversation_id, a.message_id,
                     CASE
                         WHEN a.status IN (@uploaded, @scanning, @ticketed) THEN
                             FALSE
                         WHEN a.status = @bound THEN
                             (
                                 (a.conversation_id IS NOT NULL AND EXISTS (
                                     SELECT 1 FROM {members} cm
                                     WHERE cm.conversation_id = a.conversation_id
                                       AND cm.user_id = @uid
                                 ))
                                 OR
                                 (a.message_id IS NOT NULL AND EXISTS (
                                     SELECT 1 FROM {messages} m
                                     WHERE m.message_id = a.message_id
                                       AND (
                                           m.sender_user_id = @uid
                                           OR m.receiver_user_id = @uid
                                           OR (
                                               m.conversation_id IS NOT NULL
                                               AND EXISTS (
                                                   SELECT 1 FROM {members} cm2
                                                   WHERE cm2.conversation_id = m.conversation_id
                                                     AND cm2.user_id = @uid
                                               )
                                           )
                                       )
                                 ))
                             )
                         WHEN a.status = @confirmed AND a.message_id IS NULL THEN
                             a.uploader_user_id = @uid
                         ELSE FALSE
                     END AS allowed
                 FROM {table} a
                 WHERE a.attachment_id = @id
                 """, conn);
            cmd.Parameters.AddWithValue("id", attachmentId);
            cmd.Parameters.AddWithValue("uid", userId);
            cmd.Parameters.AddWithValue("bound", (short)AttachmentStatus.Bound);
            cmd.Parameters.AddWithValue("confirmed", (short)AttachmentStatus.Confirmed);
            cmd.Parameters.AddWithValue("ticketed", (short)AttachmentStatus.Ticketed);
            cmd.Parameters.AddWithValue("uploaded", (short)AttachmentStatus.Uploaded);
            cmd.Parameters.AddWithValue("scanning", (short)AttachmentStatus.Scanning);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new AttachmentDownloadAccess(
                    attachmentId, string.Empty, "application/octet-stream", null,
                    AttachmentDownloadDecision.NotFound);
            }

            var id = reader.GetString(0);
            var objectKey = reader.GetString(1);
            var contentType = reader.GetString(2);
            var originalName = reader.IsDBNull(3) ? null : reader.GetString(3);
            var status = (AttachmentStatus)reader.GetInt16(4);
            var allowed = !reader.IsDBNull(8) && reader.GetBoolean(8);

            if (status is AttachmentStatus.Ticketed or AttachmentStatus.Uploaded or AttachmentStatus.Scanning)
            {
                return new AttachmentDownloadAccess(
                    id, objectKey, contentType, originalName,
                    AttachmentDownloadDecision.NotReady);
            }

            return new AttachmentDownloadAccess(
                id, objectKey, contentType, originalName,
                allowed ? AttachmentDownloadDecision.Allowed : AttachmentDownloadDecision.Forbidden);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogDebug(ex, "attachments/conversation_members 联查不可用，回退 uploader 鉴权");
            return await ResolveDownloadAccessUploaderFallbackAsync(
                cs!, attachmentId, userId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AttachmentDownloadAccess> ResolveDownloadAccessUploaderFallbackAsync(
        string connectionString,
        string attachmentId,
        long userId,
        CancellationToken cancellationToken)
    {
        var table = TableSql();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT attachment_id, object_key, content_type, original_name, status, uploader_user_id, message_id
             FROM {table}
             WHERE attachment_id = @id
             """, conn);
        cmd.Parameters.AddWithValue("id", attachmentId);

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new AttachmentDownloadAccess(
                    attachmentId, string.Empty, "application/octet-stream", null,
                    AttachmentDownloadDecision.NotFound);
            }

            var id = reader.GetString(0);
            var objectKey = reader.GetString(1);
            var contentType = reader.GetString(2);
            var originalName = reader.IsDBNull(3) ? null : reader.GetString(3);
            var status = (AttachmentStatus)reader.GetInt16(4);
            var uploader = reader.GetInt64(5);
            var messageId = reader.IsDBNull(6) ? null : reader.GetString(6);

            if (status is AttachmentStatus.Ticketed or AttachmentStatus.Uploaded or AttachmentStatus.Scanning)
            {
                return new AttachmentDownloadAccess(
                    id, objectKey, contentType, originalName,
                    AttachmentDownloadDecision.NotReady);
            }

            var allowed = status == AttachmentStatus.Confirmed
                          && messageId is null
                          && uploader == userId;

            return new AttachmentDownloadAccess(
                id, objectKey, contentType, originalName,
                allowed ? AttachmentDownloadDecision.Allowed : AttachmentDownloadDecision.Forbidden);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            return new AttachmentDownloadAccess(
                attachmentId, string.Empty, "application/octet-stream", null,
                AttachmentDownloadDecision.Unavailable);
        }
    }

    public async Task<IReadOnlyList<AttachmentRecord>> ListForExportAsync(
        long userId,
        int maxRows = 50_000,
        CancellationToken cancellationToken = default)
    {
        var cs = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            return [];

        maxRows = Math.Clamp(maxRows, 1, 100_000);
        var table = TableSql();
        var messages = MessagesTableSql();

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        // 上传者本人的 Confirmed/Bound，或绑定到其参与消息的附件。
        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT
                 a.attachment_id, a.uploader_user_id, a.object_key, a.public_url,
                 a.content_type, a.size_bytes, a.original_name, a.status,
                 a.message_id, a.conversation_id, a.client_attachment_id,
                 a.created_at_ms, a.confirmed_at_ms, a.bound_at_ms
             FROM {table} a
             WHERE a.status IN (@confirmed, @bound)
               AND (
                   a.uploader_user_id = @uid
                   OR (
                       a.message_id IS NOT NULL
                       AND EXISTS (
                           SELECT 1 FROM {messages} m
                           WHERE m.message_id = a.message_id
                             AND (m.sender_user_id = @uid OR m.receiver_user_id = @uid)
                       )
                   )
               )
             ORDER BY a.created_at_ms ASC, a.attachment_id ASC
             LIMIT @limit
             """, conn);

        cmd.Parameters.AddWithValue("uid", userId);
        cmd.Parameters.AddWithValue("confirmed", (short)AttachmentStatus.Confirmed);
        cmd.Parameters.AddWithValue("bound", (short)AttachmentStatus.Bound);
        cmd.Parameters.AddWithValue("limit", maxRows);

        var list = new List<AttachmentRecord>();
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add(ReadRecord(reader));
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            // messages 表可能尚未迁移：仅按 uploader 列举 Confirmed/Bound。
            _logger.LogDebug(ex, "attachments/messages 联查不可用，回退为 uploader 列举");
            return await ListForExportUploaderOnlyAsync(cs!, userId, maxRows, cancellationToken)
                .ConfigureAwait(false);
        }

        return list;
    }

    private async Task<IReadOnlyList<AttachmentRecord>> ListForExportUploaderOnlyAsync(
        string connectionString,
        long userId,
        int maxRows,
        CancellationToken cancellationToken)
    {
        var table = TableSql();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT
                 a.attachment_id, a.uploader_user_id, a.object_key, a.public_url,
                 a.content_type, a.size_bytes, a.original_name, a.status,
                 a.message_id, a.conversation_id, a.client_attachment_id,
                 a.created_at_ms, a.confirmed_at_ms, a.bound_at_ms
             FROM {table} a
             WHERE a.status IN (@confirmed, @bound)
               AND a.uploader_user_id = @uid
             ORDER BY a.created_at_ms ASC, a.attachment_id ASC
             LIMIT @limit
             """, conn);
        cmd.Parameters.AddWithValue("uid", userId);
        cmd.Parameters.AddWithValue("confirmed", (short)AttachmentStatus.Confirmed);
        cmd.Parameters.AddWithValue("bound", (short)AttachmentStatus.Bound);
        cmd.Parameters.AddWithValue("limit", maxRows);

        var list = new List<AttachmentRecord>();
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add(ReadRecord(reader));
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogDebug(ex, "attachments 表不可用，跳过 formal 导出");
            return [];
        }

        return list;
    }

    public async Task<IReadOnlyList<string>> ListObjectKeysForUserAsync(
        long uploaderUserId,
        CancellationToken cancellationToken = default)
    {
        var cs = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            return [];

        var table = TableSql();
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT object_key FROM {table}
             WHERE uploader_user_id = @uid
               AND status <> @abandoned
             """, conn);
        cmd.Parameters.AddWithValue("uid", uploaderUserId);
        cmd.Parameters.AddWithValue("abandoned", (short)AttachmentStatus.Abandoned);

        var keys = new List<string>();
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                keys.Add(reader.GetString(0));
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogDebug(ex, "attachments 表不可用，跳过 object_key 列举");
            return [];
        }

        return keys;
    }

    public async Task<IReadOnlySet<string>> ListActiveObjectKeysAsync(
        CancellationToken cancellationToken = default)
    {
        var cs = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            return new HashSet<string>(StringComparer.Ordinal);

        var table = TableSql();
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT object_key FROM {table}
             WHERE status IN (@confirmed, @bound)
             """, conn);
        cmd.Parameters.AddWithValue("confirmed", (short)AttachmentStatus.Confirmed);
        cmd.Parameters.AddWithValue("bound", (short)AttachmentStatus.Bound);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                keys.Add(reader.GetString(0));
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogDebug(ex, "attachments 表不可用，跳过 active object_key 列举");
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return keys;
    }

    public async Task MarkAbandonedAsync(
        IReadOnlyList<string> attachmentIds,
        CancellationToken cancellationToken = default)
    {
        if (attachmentIds.Count == 0) return;
        var cs = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return;

        var table = TableSql();
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
             UPDATE {table}
             SET status = @abandoned
             WHERE attachment_id = ANY(@ids)
             """, conn);
        cmd.Parameters.AddWithValue("abandoned", (short)AttachmentStatus.Abandoned);
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = attachmentIds.ToArray(),
        });

        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogDebug(ex, "attachments 表不可用，跳过 MarkAbandoned");
        }
    }

    public async Task MarkAbandonedByUploaderAsync(
        long uploaderUserId,
        CancellationToken cancellationToken = default)
    {
        var cs = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return;

        var table = TableSql();
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
             UPDATE {table}
             SET status = @abandoned
             WHERE uploader_user_id = @uid
               AND status <> @abandoned
             """, conn);
        cmd.Parameters.AddWithValue("abandoned", (short)AttachmentStatus.Abandoned);
        cmd.Parameters.AddWithValue("uid", uploaderUserId);

        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogDebug(ex, "attachments 表不可用，跳过 MarkAbandonedByUploader");
        }
    }

    public async Task<string?> TryAbandonUnboundByUploaderAsync(
        string attachmentId,
        long uploaderUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(attachmentId))
            return null;

        var cs = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            return null;

        var table = TableSql();
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
             UPDATE {table}
             SET status = @abandoned
             WHERE attachment_id = @aid
               AND uploader_user_id = @uid
               AND message_id IS NULL
               AND status IN (@ticketed, @uploaded, @scanning, @confirmed)
             RETURNING object_key
             """, conn);
        cmd.Parameters.AddWithValue("abandoned", (short)AttachmentStatus.Abandoned);
        cmd.Parameters.AddWithValue("aid", attachmentId);
        cmd.Parameters.AddWithValue("uid", uploaderUserId);
        cmd.Parameters.AddWithValue("ticketed", (short)AttachmentStatus.Ticketed);
        cmd.Parameters.AddWithValue("uploaded", (short)AttachmentStatus.Uploaded);
        cmd.Parameters.AddWithValue("scanning", (short)AttachmentStatus.Scanning);
        cmd.Parameters.AddWithValue("confirmed", (short)AttachmentStatus.Confirmed);

        try
        {
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is string key && !string.IsNullOrWhiteSpace(key) ? key : null;
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogDebug(ex, "attachments 表不可用，跳过 TryAbandonUnboundByUploader");
            return null;
        }
    }

    public async Task<IReadOnlyList<AttachmentAbandonBatchItem>> AbandonAgedUnboundAsync(
        TimeSpan maxAge,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        batchSize = Math.Clamp(batchSize, 1, 200);
        if (maxAge <= TimeSpan.Zero)
            return [];

        var cs = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            return [];

        var cutoffMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            - (long)Math.Max(0, maxAge.TotalMilliseconds);
        var table = TableSql();

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var cmd = new NpgsqlCommand(
                $"""
                 UPDATE {table} AS a
                 SET status = @abandoned
                 FROM (
                     SELECT attachment_id
                     FROM {table}
                     WHERE status IN (@ticketed, @confirmed)
                       AND message_id IS NULL
                       AND created_at_ms <= @cutoff_ms
                     ORDER BY created_at_ms
                     LIMIT @batch
                     FOR UPDATE SKIP LOCKED
                 ) AS batch
                 WHERE a.attachment_id = batch.attachment_id
                 RETURNING a.attachment_id, a.object_key, a.uploader_user_id;
                 """,
                conn,
                tx);
            cmd.Parameters.AddWithValue("abandoned", (short)AttachmentStatus.Abandoned);
            cmd.Parameters.AddWithValue("ticketed", (short)AttachmentStatus.Ticketed);
            cmd.Parameters.AddWithValue("confirmed", (short)AttachmentStatus.Confirmed);
            cmd.Parameters.AddWithValue("cutoff_ms", cutoffMs);
            cmd.Parameters.AddWithValue("batch", batchSize);

            var items = new List<AttachmentAbandonBatchItem>(batchSize);
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var id = reader.GetString(0);
                    var key = reader.GetString(1);
                    var uploader = reader.GetInt64(2);
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(key))
                        items.Add(new AttachmentAbandonBatchItem(id, key, uploader));
                }
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return items;
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug(ex, "attachments 表不可用，跳过 AbandonAgedUnbound");
            return [];
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<AttachmentOpsOrphanQueryResult> QueryOpsOrphansAsync(
        TimeSpan orphanAge,
        TimeSpan stuckScanningAge,
        int sampleLimit,
        CancellationToken cancellationToken = default)
    {
        sampleLimit = Math.Clamp(sampleLimit, 1, 20);
        var cs = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
        {
            return UnavailableOrphanResult(UnavailableReason);
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var orphanCutoffMs = nowMs - (long)Math.Max(0, orphanAge.TotalMilliseconds);
        var stuckCutoffMs = nowMs - (long)Math.Max(0, stuckScanningAge.TotalMilliseconds);
        var table = TableSql();

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            long confirmedUnbound = 0, uploading = 0, stuck = 0;
            long? oldestConfirmed = null, oldestUploading = null, oldestStuck = null;
            long activeCount = 0, activeBytes = 0;

            await using (var agg = new NpgsqlCommand(
                             $"""
                              SELECT
                                COUNT(*) FILTER (
                                  WHERE status = @confirmed
                                    AND message_id IS NULL
                                    AND created_at_ms <= @orphanCutoff) AS confirmed_unbound,
                                MIN(created_at_ms) FILTER (
                                  WHERE status = @confirmed
                                    AND message_id IS NULL
                                    AND created_at_ms <= @orphanCutoff) AS oldest_confirmed,
                                COUNT(*) FILTER (
                                  WHERE status IN (@ticketed, @uploaded)
                                    AND created_at_ms <= @orphanCutoff) AS uploading,
                                MIN(created_at_ms) FILTER (
                                  WHERE status IN (@ticketed, @uploaded)
                                    AND created_at_ms <= @orphanCutoff) AS oldest_uploading,
                                COUNT(*) FILTER (
                                  WHERE status = @scanning
                                    AND created_at_ms <= @stuckCutoff) AS stuck_scanning,
                                MIN(created_at_ms) FILTER (
                                  WHERE status = @scanning
                                    AND created_at_ms <= @stuckCutoff) AS oldest_stuck,
                                COUNT(*) FILTER (
                                  WHERE status IN (@confirmed, @bound)) AS active_count,
                                COALESCE(SUM(size_bytes) FILTER (
                                  WHERE status IN (@confirmed, @bound)), 0) AS active_bytes
                              FROM {table}
                              """, conn))
            {
                AddOrphanParams(agg, orphanCutoffMs, stuckCutoffMs);
                await using var reader = await agg.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    confirmedUnbound = reader.GetInt64(0);
                    oldestConfirmed = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                    uploading = reader.GetInt64(2);
                    oldestUploading = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                    stuck = reader.GetInt64(4);
                    oldestStuck = reader.IsDBNull(5) ? null : reader.GetInt64(5);
                    activeCount = reader.GetInt64(6);
                    activeBytes = reader.GetInt64(7);
                }
            }

            var worstConfirmed = await LoadOrphanSamplesAsync(
                    conn,
                    table,
                    "status = @confirmed AND message_id IS NULL AND created_at_ms <= @orphanCutoff",
                    orphanCutoffMs,
                    stuckCutoffMs,
                    sampleLimit,
                    cancellationToken)
                .ConfigureAwait(false);
            var worstUploading = await LoadOrphanSamplesAsync(
                    conn,
                    table,
                    "status IN (@ticketed, @uploaded) AND created_at_ms <= @orphanCutoff",
                    orphanCutoffMs,
                    stuckCutoffMs,
                    sampleLimit,
                    cancellationToken)
                .ConfigureAwait(false);
            var worstStuck = await LoadOrphanSamplesAsync(
                    conn,
                    table,
                    "status = @scanning AND created_at_ms <= @stuckCutoff",
                    orphanCutoffMs,
                    stuckCutoffMs,
                    sampleLimit,
                    cancellationToken)
                .ConfigureAwait(false);

            return new AttachmentOpsOrphanQueryResult(
                Available: true,
                UnavailableReason: null,
                ConfirmedUnboundPastAgeCount: confirmedUnbound,
                AbandonedUploadingPastAgeCount: uploading,
                StuckScanningCount: stuck,
                OldestConfirmedUnboundAtMs: oldestConfirmed,
                OldestUploadingAtMs: oldestUploading,
                OldestStuckScanningAtMs: oldestStuck,
                ActiveAttachmentCount: activeCount,
                ActiveSizeBytesSum: activeBytes,
                WorstConfirmedUnbound: worstConfirmed,
                WorstUploading: worstUploading,
                WorstStuckScanning: worstStuck);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogDebug(ex, "attachments 表不可用，跳过 ops orphan 查询");
            return UnavailableOrphanResult("attachments table unavailable");
        }
    }

    private static AttachmentOpsOrphanQueryResult UnavailableOrphanResult(string reason) =>
        new(
            Available: false,
            UnavailableReason: reason,
            ConfirmedUnboundPastAgeCount: 0,
            AbandonedUploadingPastAgeCount: 0,
            StuckScanningCount: 0,
            OldestConfirmedUnboundAtMs: null,
            OldestUploadingAtMs: null,
            OldestStuckScanningAtMs: null,
            ActiveAttachmentCount: 0,
            ActiveSizeBytesSum: 0,
            WorstConfirmedUnbound: [],
            WorstUploading: [],
            WorstStuckScanning: []);

    private static void AddOrphanParams(NpgsqlCommand cmd, long orphanCutoffMs, long stuckCutoffMs)
    {
        cmd.Parameters.AddWithValue("confirmed", (short)AttachmentStatus.Confirmed);
        cmd.Parameters.AddWithValue("bound", (short)AttachmentStatus.Bound);
        cmd.Parameters.AddWithValue("ticketed", (short)AttachmentStatus.Ticketed);
        cmd.Parameters.AddWithValue("uploaded", (short)AttachmentStatus.Uploaded);
        cmd.Parameters.AddWithValue("scanning", (short)AttachmentStatus.Scanning);
        cmd.Parameters.AddWithValue("orphanCutoff", orphanCutoffMs);
        cmd.Parameters.AddWithValue("stuckCutoff", stuckCutoffMs);
    }

    private static async Task<IReadOnlyList<AttachmentOpsOrphanSample>> LoadOrphanSamplesAsync(
        NpgsqlConnection conn,
        string table,
        string whereSql,
        long orphanCutoffMs,
        long stuckCutoffMs,
        int sampleLimit,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT attachment_id, object_key, uploader_user_id, status, size_bytes, created_at_ms
             FROM {table}
             WHERE {whereSql}
             ORDER BY created_at_ms ASC
             LIMIT @limit
             """, conn);
        AddOrphanParams(cmd, orphanCutoffMs, stuckCutoffMs);
        cmd.Parameters.AddWithValue("limit", sampleLimit);

        var rows = new List<AttachmentOpsOrphanSample>(sampleLimit);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new AttachmentOpsOrphanSample(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt16(3),
                reader.GetInt64(4),
                reader.GetInt64(5)));
        }

        return rows;
    }

    private static AttachmentRecord ReadRecord(NpgsqlDataReader reader) => new(
        AttachmentId: reader.GetString(0),
        UploaderUserId: reader.GetInt64(1),
        ObjectKey: reader.GetString(2),
        PublicUrl: reader.IsDBNull(3) ? null : reader.GetString(3),
        ContentType: reader.GetString(4),
        SizeBytes: reader.GetInt64(5),
        OriginalName: reader.IsDBNull(6) ? null : reader.GetString(6),
        Status: (AttachmentStatus)reader.GetInt16(7),
        MessageId: reader.IsDBNull(8) ? null : reader.GetString(8),
        ConversationId: reader.IsDBNull(9) ? null : reader.GetString(9),
        ClientAttachmentId: reader.IsDBNull(10) ? null : reader.GetString(10),
        CreatedAtMs: reader.GetInt64(11),
        ConfirmedAtMs: reader.IsDBNull(12) ? null : reader.GetInt64(12),
        BoundAtMs: reader.IsDBNull(13) ? null : reader.GetInt64(13));

    private string RequireConnectionString() =>
        ResolveConnectionString()
        ?? throw new InvalidOperationException(UnavailableReason);

    private string? ResolveConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(_export.RealtimeConnectionString))
            return _export.RealtimeConnectionString;
        if (!string.IsNullOrWhiteSpace(_evidence.RealtimeConnectionString))
            return _evidence.RealtimeConnectionString;
        return null;
    }

    private string Schema() =>
        string.IsNullOrWhiteSpace(_evidence.Schema) ? "realtime" : _evidence.Schema.Trim();

    private string TableSql() => $"\"{Schema()}\".\"attachments\"";

    private string MessagesTableSql() => $"\"{Schema()}\".\"messages\"";

    private string MembersTableSql() => $"\"{Schema()}\".\"conversation_members\"";
}
