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
public sealed class RealtimeAttachmentMetadataStore :
    IAttachmentMetadataStore,
    IAttachmentScanProjectionMetadataStore,
    IAttachmentMetadataHealthProbe
{
    private const long AttachmentQuotaLockNamespace = 0x4154545100000000L;

    private readonly MessageEvidenceOptions _evidence;
    private readonly DataExportStorageOptions _export;
    private readonly AttachmentStorageOptions _attachments;
    private readonly ILogger<RealtimeAttachmentMetadataStore> _logger;
    private readonly NpgsqlDataSource? _dataSource;

    public RealtimeAttachmentMetadataStore(
        IOptions<MessageEvidenceOptions> evidence,
        IOptions<DataExportStorageOptions> export,
        IOptions<AttachmentStorageOptions> attachments,
        ILogger<RealtimeAttachmentMetadataStore> logger,
        RealtimePostgresDataSource? sharedDataSource = null)
    {
        _evidence = evidence.Value;
        _export = export.Value;
        _attachments = attachments.Value;
        _logger = logger;
        _dataSource = sharedDataSource?.DataSource;
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(ResolveConnectionString());

    public string UnavailableReason =>
        IsAvailable
            ? string.Empty
            : "未配置 MessageEvidence:RealtimeConnectionString / DataExport:RealtimeConnectionString";

    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AttachmentDedupCandidate?> TryFindDedupCandidateAsync(
        long uploaderUserId,
        string sha256Hex,
        CancellationToken cancellationToken = default)
    {
        if (uploaderUserId <= 0
            || string.IsNullOrWhiteSpace(sha256Hex)
            || sha256Hex.Length != 64)
            return null;

        var cs = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            return null;

        var table = TableSql();
        await using var conn = CreateConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT attachment_id, object_key, content_type, size_bytes
             FROM {table}
             WHERE uploader_user_id = @uid
               AND content_hash = lower(@hash)
               AND status IN (@confirmed, @bound)
             ORDER BY COALESCE(confirmed_at_ms, created_at_ms) DESC, attachment_id DESC
             LIMIT 1
             """, conn);
        cmd.Parameters.AddWithValue("uid", uploaderUserId);
        cmd.Parameters.AddWithValue("hash", sha256Hex);
        cmd.Parameters.AddWithValue("confirmed", (short)AttachmentStatus.Confirmed);
        cmd.Parameters.AddWithValue("bound", (short)AttachmentStatus.Bound);

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            var attachmentId = reader.GetString(0);
            var objectKey = reader.GetString(1);
            var contentType = reader.GetString(2);
            var sizeBytes = reader.GetInt64(3);
            return string.IsNullOrWhiteSpace(attachmentId) || string.IsNullOrWhiteSpace(objectKey)
                ? null
                : new AttachmentDedupCandidate(attachmentId, objectKey, contentType, sizeBytes);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            // 老表无 content_hash 列或 attachments 表不可用：秒传降级为普通上传。
            _logger.LogDebug(ex, "content_hash 列不可用，秒传降级为普通上传");
            return null;
        }
    }

    public async Task<AttachmentUploadReservationStatus> ReserveTicketedAsync(
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
        if (sizeBytes <= 0 || sizeBytes > _attachments.MaxBytes)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));

        var cs = RequireConnectionString();
        var table = TableSql();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // PUT 预签名不能约束实际 Content-Length；Ticketed 按单对象上限预留，
        // 上传后再收敛为实际大小，避免客户端用小声明绕过总字节配额。
        var reservationBytes = _attachments.MaxBytes;

        await using var conn = CreateConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // 必须先在独立语句中等待用户锁。若把锁放进后续 CTE，READ COMMITTED
        // 会在等待前取得旧快照，两个实例仍可能同时通过配额检查。
        await using (var quotaLock = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(@lockKey)",
                         conn,
                         transaction))
        {
            quotaLock.Parameters.AddWithValue(
                "lockKey",
                AttachmentQuotaLockNamespace ^ uploaderUserId);
            await quotaLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long unconfirmedCount;
        long activeBytes;
        bool inserted;

        await using (var cmd = new NpgsqlCommand(
                         $"""
                          WITH usage AS MATERIALIZED (
                              SELECT
                                  COUNT(*) FILTER (
                                      WHERE status IN (@ticketed, @uploaded, @scanning)
                                  )::bigint AS unconfirmed_count,
                                  COALESCE(SUM(size_bytes) FILTER (
                                      WHERE status IN (
                                          @ticketed, @uploaded, @scanning, @confirmed, @bound)
                                  ), 0)::bigint AS active_bytes
                              FROM {table}
                              WHERE uploader_user_id = @uid
                          ),
                          inserted AS (
                              INSERT INTO {table} (
                                  attachment_id, uploader_user_id, object_key, public_url,
                                  content_type, size_bytes, original_name, status,
                                  message_id, conversation_id, client_attachment_id,
                                  created_at_ms, confirmed_at_ms, bound_at_ms)
                              SELECT
                                  @id, @uid, @key, @url,
                                  @ct, @reservationBytes, @name, @ticketed,
                                  NULL, NULL, @clientId,
                                  @created, NULL, NULL
                              FROM usage
                              WHERE unconfirmed_count < @maxUnconfirmed
                                AND active_bytes <= @maxStorageBytes - @reservationBytes
                              ON CONFLICT (attachment_id) DO NOTHING
                              RETURNING 1
                          )
                          SELECT
                              usage.unconfirmed_count,
                              usage.active_bytes,
                              EXISTS (SELECT 1 FROM inserted)
                          FROM usage
                          """,
                         conn,
                         transaction))
        {
            cmd.Parameters.AddWithValue("id", attachmentId);
            cmd.Parameters.AddWithValue("uid", uploaderUserId);
            cmd.Parameters.AddWithValue("key", objectKey);
            cmd.Parameters.AddWithValue("url", (object?)publicUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ct", contentType);
            cmd.Parameters.AddWithValue("reservationBytes", reservationBytes);
            cmd.Parameters.AddWithValue("name", (object?)originalName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ticketed", (short)AttachmentStatus.Ticketed);
            cmd.Parameters.AddWithValue("uploaded", (short)AttachmentStatus.Uploaded);
            cmd.Parameters.AddWithValue("scanning", (short)AttachmentStatus.Scanning);
            cmd.Parameters.AddWithValue("confirmed", (short)AttachmentStatus.Confirmed);
            cmd.Parameters.AddWithValue("bound", (short)AttachmentStatus.Bound);
            cmd.Parameters.AddWithValue("clientId", (object?)clientAttachmentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("created", nowMs);
            cmd.Parameters.AddWithValue("maxUnconfirmed", _attachments.MaxUnconfirmedObjectsPerUser);
            cmd.Parameters.AddWithValue("maxStorageBytes", _attachments.MaxStorageBytesPerUser);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("附件配额查询未返回结果");

            unconfirmedCount = reader.GetInt64(0);
            activeBytes = reader.GetInt64(1);
            inserted = reader.GetBoolean(2);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (inserted)
            return AttachmentUploadReservationStatus.Reserved;
        if (unconfirmedCount >= _attachments.MaxUnconfirmedObjectsPerUser)
            return AttachmentUploadReservationStatus.UnconfirmedObjectLimitExceeded;
        if (activeBytes > _attachments.MaxStorageBytesPerUser - reservationBytes)
            return AttachmentUploadReservationStatus.StorageBytesLimitExceeded;

        throw new InvalidOperationException($"附件上传预留冲突。AttachmentId={attachmentId}");
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

        await using var conn = CreateConnection(cs);
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
            throw new InvalidOperationException(
                $"附件确认状态迁移失败。AttachmentId={attachmentId}, UserId={uploaderUserId}");
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
        await using var conn = CreateConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Ticketed → Uploaded → Scanning for upload confirmation. A durable scan
        // projection may retry after Realtime has already reached a terminal state;
        // in that case retain the terminal status while still writing full-stream SHA-256.
        await using var cmd = new NpgsqlCommand(
            $"""
             UPDATE {table}
             SET status = CASE
                     WHEN status IN (@ticketed, @uploaded, @scanning) THEN @scanning
                     ELSE status
                 END,
                 size_bytes = CASE WHEN @size > 0 THEN @size ELSE size_bytes END,
                 content_hash = CASE
                     WHEN @hash IS NOT NULL AND length(@hash) > 0 THEN lower(@hash)
                     ELSE content_hash
                 END
             WHERE attachment_id = @id
               AND uploader_user_id = @uid
               AND status IN (@ticketed, @uploaded, @scanning, @confirmed, @rejected, @bound)
             """, conn);
        cmd.Parameters.AddWithValue("id", attachmentId);
        cmd.Parameters.AddWithValue("uid", uploaderUserId);
        cmd.Parameters.AddWithValue("size", sizeBytes);
        cmd.Parameters.Add("hash", NpgsqlDbType.Varchar).Value =
            string.IsNullOrWhiteSpace(sha256Hex) ? DBNull.Value : sha256Hex.Trim();
        cmd.Parameters.AddWithValue("scanning", (short)AttachmentStatus.Scanning);
        cmd.Parameters.AddWithValue("ticketed", (short)AttachmentStatus.Ticketed);
        cmd.Parameters.AddWithValue("uploaded", (short)AttachmentStatus.Uploaded);
        cmd.Parameters.AddWithValue("confirmed", (short)AttachmentStatus.Confirmed);
        cmd.Parameters.AddWithValue("rejected", (short)AttachmentStatus.Rejected);
        cmd.Parameters.AddWithValue("bound", (short)AttachmentStatus.Bound);

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
            throw new InvalidOperationException(
                $"附件扫描状态迁移失败。AttachmentId={attachmentId}, UserId={uploaderUserId}");
    }

    public async Task MarkRejectedAsync(
        string attachmentId,
        long uploaderUserId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var cs = RequireConnectionString();
        var table = TableSql();
        await using var conn = CreateConnection(cs);
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

    public async Task<AttachmentProjectionWriteResult> MarkUploadedScanningAsync(
        string attachmentId,
        long uploaderUserId,
        long sizeBytes,
        long projectionId,
        long scanVersion,
        string? sha256Hex = null,
        CancellationToken cancellationToken = default)
    {
        var cs = RequireConnectionString();
        var table = TableSql();
        await using var conn = CreateConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(
            $"""
             UPDATE {table} AS a
             SET status = CASE
                     WHEN a.status IN (@ticketed, @uploaded, @scanning)
                         THEN @scanning
                     ELSE a.status
                 END,
                 size_bytes = CASE WHEN @size > 0 THEN @size ELSE a.size_bytes END,
                 content_hash = CASE
                     WHEN @hash IS NOT NULL AND length(@hash) > 0 THEN lower(@hash)
                     ELSE a.content_hash
                 END,
                 scan_projection_id = @projection_id,
                 scan_version = @scan_version
             WHERE a.attachment_id = @id
               AND a.uploader_user_id = @uid
               AND a.status IN (@ticketed, @uploaded, @scanning, @confirmed, @rejected, @bound)
               AND (
                   a.scan_version < @scan_version
                   OR (
                       a.scan_version = @scan_version
                       AND (a.scan_projection_id IS NULL OR a.scan_projection_id = @projection_id)
                   )
               )
             """, conn);
        cmd.Parameters.AddWithValue("id", attachmentId);
        cmd.Parameters.AddWithValue("uid", uploaderUserId);
        cmd.Parameters.AddWithValue("size", sizeBytes);
        cmd.Parameters.Add("hash", NpgsqlDbType.Varchar).Value =
            string.IsNullOrWhiteSpace(sha256Hex) ? DBNull.Value : sha256Hex.Trim();
        cmd.Parameters.AddWithValue("projection_id", projectionId);
        cmd.Parameters.AddWithValue("scan_version", scanVersion);
        cmd.Parameters.AddWithValue("ticketed", (short)AttachmentStatus.Ticketed);
        cmd.Parameters.AddWithValue("uploaded", (short)AttachmentStatus.Uploaded);
        cmd.Parameters.AddWithValue("scanning", (short)AttachmentStatus.Scanning);
        cmd.Parameters.AddWithValue("confirmed", (short)AttachmentStatus.Confirmed);
        cmd.Parameters.AddWithValue("rejected", (short)AttachmentStatus.Rejected);
        cmd.Parameters.AddWithValue("bound", (short)AttachmentStatus.Bound);

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows == 1
            ? AttachmentProjectionWriteResult.Applied
            : await ResolveProjectionWriteResultAsync(
                    conn,
                    table,
                    attachmentId,
                    uploaderUserId,
                    projectionId,
                    scanVersion,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task<AttachmentProjectionWriteResult> ConfirmAsync(
        string attachmentId,
        long uploaderUserId,
        string objectKey,
        string? publicUrl,
        string contentType,
        long sizeBytes,
        string? originalName,
        long projectionId,
        long scanVersion,
        CancellationToken cancellationToken = default)
    {
        var cs = RequireConnectionString();
        var table = TableSql();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var conn = CreateConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(
            $"""
             UPDATE {table} AS a
             SET object_key = @key,
                 public_url = @url,
                 content_type = @ct,
                 size_bytes = CASE WHEN @size > 0 THEN @size ELSE a.size_bytes END,
                 original_name = COALESCE(@name, a.original_name),
                 status = @confirmed,
                 confirmed_at_ms = COALESCE(a.confirmed_at_ms, @confirmed_at),
                 scan_projection_id = @projection_id,
                 scan_version = @scan_version
             WHERE a.attachment_id = @id
               AND a.uploader_user_id = @uid
               AND a.status IN (@ticketed, @uploaded, @scanning, @confirmed)
               AND (
                   a.scan_version < @scan_version
                   OR (
                       a.scan_version = @scan_version
                       AND (a.scan_projection_id IS NULL OR a.scan_projection_id = @projection_id)
                   )
               )
             """, conn);
        cmd.Parameters.AddWithValue("id", attachmentId);
        cmd.Parameters.AddWithValue("uid", uploaderUserId);
        cmd.Parameters.AddWithValue("key", objectKey);
        cmd.Parameters.AddWithValue("url", (object?)publicUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ct", contentType);
        cmd.Parameters.AddWithValue("size", sizeBytes);
        cmd.Parameters.AddWithValue("name", (object?)originalName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("confirmed", (short)AttachmentStatus.Confirmed);
        cmd.Parameters.AddWithValue("ticketed", (short)AttachmentStatus.Ticketed);
        cmd.Parameters.AddWithValue("uploaded", (short)AttachmentStatus.Uploaded);
        cmd.Parameters.AddWithValue("scanning", (short)AttachmentStatus.Scanning);
        cmd.Parameters.AddWithValue("confirmed_at", nowMs);
        cmd.Parameters.AddWithValue("projection_id", projectionId);
        cmd.Parameters.AddWithValue("scan_version", scanVersion);

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows == 1
            ? AttachmentProjectionWriteResult.Applied
            : await ResolveProjectionWriteResultAsync(
                    conn,
                    table,
                    attachmentId,
                    uploaderUserId,
                    projectionId,
                    scanVersion,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task<AttachmentProjectionWriteResult> MarkRejectedAsync(
        string attachmentId,
        long uploaderUserId,
        string? reason,
        long projectionId,
        long scanVersion,
        CancellationToken cancellationToken = default)
    {
        var cs = RequireConnectionString();
        var table = TableSql();
        await using var conn = CreateConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(
            $"""
             UPDATE {table} AS a
             SET status = @rejected,
                 scan_projection_id = @projection_id,
                 scan_version = @scan_version
             WHERE a.attachment_id = @id
               AND a.uploader_user_id = @uid
               AND a.status IN (@ticketed, @uploaded, @scanning, @rejected)
               AND (
                   a.scan_version < @scan_version
                   OR (
                       a.scan_version = @scan_version
                       AND (a.scan_projection_id IS NULL OR a.scan_projection_id = @projection_id)
                   )
               )
             """, conn);
        cmd.Parameters.AddWithValue("id", attachmentId);
        cmd.Parameters.AddWithValue("uid", uploaderUserId);
        cmd.Parameters.AddWithValue("rejected", (short)AttachmentStatus.Rejected);
        cmd.Parameters.AddWithValue("ticketed", (short)AttachmentStatus.Ticketed);
        cmd.Parameters.AddWithValue("uploaded", (short)AttachmentStatus.Uploaded);
        cmd.Parameters.AddWithValue("scanning", (short)AttachmentStatus.Scanning);
        cmd.Parameters.AddWithValue("projection_id", projectionId);
        cmd.Parameters.AddWithValue("scan_version", scanVersion);

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows == 1
            ? AttachmentProjectionWriteResult.Applied
            : await ResolveProjectionWriteResultAsync(
                    conn,
                    table,
                    attachmentId,
                    uploaderUserId,
                    projectionId,
                    scanVersion,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task<AttachmentProjectionWriteResult> MarkAbandonedAsync(
        string attachmentId,
        long uploaderUserId,
        long projectionId,
        long scanVersion,
        CancellationToken cancellationToken = default)
    {
        var cs = RequireConnectionString();
        var table = TableSql();
        await using var conn = CreateConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(
            $"""
             UPDATE {table} AS a
             SET status = @abandoned,
                 scan_projection_id = @projection_id,
                 scan_version = @scan_version
             WHERE a.attachment_id = @id
               AND a.uploader_user_id = @uid
               AND a.status <> @abandoned
               AND (
                   a.scan_version < @scan_version
                   OR (
                       a.scan_version = @scan_version
                       AND (a.scan_projection_id IS NULL OR a.scan_projection_id = @projection_id)
                   )
               )
             """, conn);
        cmd.Parameters.AddWithValue("id", attachmentId);
        cmd.Parameters.AddWithValue("uid", uploaderUserId);
        cmd.Parameters.AddWithValue("abandoned", (short)AttachmentStatus.Abandoned);
        cmd.Parameters.AddWithValue("projection_id", projectionId);
        cmd.Parameters.AddWithValue("scan_version", scanVersion);

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows == 1
            ? AttachmentProjectionWriteResult.Applied
            : await ResolveProjectionWriteResultAsync(
                    conn,
                    table,
                    attachmentId,
                    uploaderUserId,
                    projectionId,
                    scanVersion,
                    cancellationToken)
                .ConfigureAwait(false);
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

        await using var conn = CreateConnection(cs);
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

    public async Task<AttachmentRecord?> GetStatusForUploaderAsync(
        string attachmentId,
        long uploaderUserId,
        CancellationToken cancellationToken = default)
    {
        var cs = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(cs)
            || string.IsNullOrWhiteSpace(attachmentId)
            || uploaderUserId <= 0)
            return null;

        await using var conn = CreateConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT attachment_id, uploader_user_id, object_key, public_url,
                    content_type, size_bytes, original_name, status,
                    message_id, conversation_id, client_attachment_id,
                    created_at_ms, confirmed_at_ms, bound_at_ms
             FROM {TableSql()}
             WHERE attachment_id = @id AND uploader_user_id = @uid
             """, conn);
        cmd.Parameters.AddWithValue("id", attachmentId);
        cmd.Parameters.AddWithValue("uid", uploaderUserId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRecord(reader)
            : null;
    }

    private async Task<AttachmentDownloadAccess> ResolveDownloadAccessUploaderFallbackAsync(
        string connectionString,
        string attachmentId,
        long userId,
        CancellationToken cancellationToken)
    {
        var table = TableSql();
        await using var conn = CreateConnection(connectionString);
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

        await using var conn = CreateConnection(cs);
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
        await using var conn = CreateConnection(connectionString);
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
        await using var conn = CreateConnection(cs);
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
        await using var conn = CreateConnection(cs);
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
        await using var conn = CreateConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
             UPDATE {table}
             SET status = @abandoned,
                 scan_version = CASE
                     WHEN status <> @abandoned THEN scan_version + 1
                     ELSE scan_version
                 END,
                 scan_projection_id = CASE
                     WHEN status <> @abandoned THEN NULL
                     ELSE scan_projection_id
                 END
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
        await using var conn = CreateConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
             UPDATE {table}
             SET status = @abandoned,
                 scan_version = CASE
                     WHEN status <> @abandoned THEN scan_version + 1
                     ELSE scan_version
                 END,
                 scan_projection_id = CASE
                     WHEN status <> @abandoned THEN NULL
                     ELSE scan_projection_id
                 END
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
        await using var conn = CreateConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
             UPDATE {table}
             SET status = @abandoned,
                 scan_version = scan_version + 1,
                 scan_projection_id = NULL
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

        await using var conn = CreateConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var cmd = new NpgsqlCommand(
                $"""
                 UPDATE {table} AS a
                 SET status = @abandoned,
                     scan_version = CASE
                         WHEN a.status <> @abandoned THEN a.scan_version + 1
                         ELSE a.scan_version
                     END,
                     scan_projection_id = CASE
                         WHEN a.status <> @abandoned THEN NULL
                         ELSE a.scan_projection_id
                     END
                 FROM (
                     SELECT attachment_id
                     FROM {table}
                      WHERE status IN (@ticketed, @confirmed, @abandoned)
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

        await using var conn = CreateConnection(cs);
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

    private static async Task<AttachmentProjectionWriteResult> ResolveProjectionWriteResultAsync(
        NpgsqlConnection connection,
        string table,
        string attachmentId,
        long uploaderUserId,
        long projectionId,
        long scanVersion,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT scan_version, scan_projection_id
             FROM {table}
             WHERE attachment_id = @id
               AND uploader_user_id = @uid
             """,
            connection);
        cmd.Parameters.AddWithValue("id", attachmentId);
        cmd.Parameters.AddWithValue("uid", uploaderUserId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return AttachmentProjectionWriteResult.NotFound;

        // A zero-row CAS means another generation already owns the target (or
        // the row is in a terminal state that this projection must not replace).
        // Treat it as an idempotent supersession, never as permission to retry a
        // stale write indefinitely.
        _ = scanVersion;
        _ = projectionId;
        return AttachmentProjectionWriteResult.AlreadySuperseded;
    }

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

    private NpgsqlConnection CreateConnection(string? connectionString = null) =>
        _dataSource?.CreateConnection()
        ?? new NpgsqlConnection(connectionString ?? RequireConnectionString());

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
