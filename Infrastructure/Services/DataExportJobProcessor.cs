using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models.Auth;
using Core.Models.Export;
using Core.Settings;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Infrastructure.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

internal sealed class DataExportCancellationRequestedException : Exception
{
    public DataExportCancellationRequestedException()
        : base("导出作业已请求取消") { }
}

// Durable export processing helpers shared by DataExportJobStore and the
// common LeasedJobExecutor. The worker loop lives only in DataExportWorker.
internal static class DataExportJobProcessor
{

    internal static async Task<(DataExportJob Job, string LeaseToken)?> ClaimOneNpgsqlAsync(
        UserDbContext db,
        string owner,
        string leaseToken,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE "T_DataExportJob" AS j
            SET "Status" = CASE
                WHEN j."Status" = 'CancelRequested' THEN 'CancelRequested'
                ELSE 'Processing'
            END,
                "LeaseOwner" = @owner,
                "LeaseToken" = @lease_token,
                "LeaseUntil" = @lease_until,
                "AttemptCount" = j."AttemptCount" + 1
            WHERE j."Id" = (
                SELECT c."Id"
                FROM "T_DataExportJob" AS c
                WHERE (c."Status" = 'Pending' AND c."NextAttemptAt" <= @now)
                   OR (c."Status" = 'Processing'
                       AND (c."LeaseUntil" IS NULL OR c."LeaseUntil" < @now))
                   OR (c."Status" = 'CancelRequested'
                       AND (c."LeaseUntil" IS NULL OR c."LeaseUntil" < @now))
                ORDER BY c."CreatedAt", c."Id"
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING j."Id", j."UserId", j."Status", j."AttemptCount";
            """;

        AddParameter(command, "owner", owner);
        AddParameter(command, "lease_token", leaseToken);
        AddParameter(command, "lease_until", leaseUntil);
        AddParameter(command, "now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var job = new DataExportJob
        {
            Id = reader.GetString(0),
            UserId = reader.GetInt64(1),
            Status = reader.GetString(2),
            LeaseOwner = owner,
            LeaseToken = leaseToken,
            LeaseUntil = leaseUntil,
            AttemptCount = reader.GetInt32(3),
        };
        return (job, leaseToken);
    }

    private static void AddParameter(
        System.Data.Common.DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }


    internal static async Task ProcessJobAsync(
        UserDbContext db,
        IDataExportBlobStore blob,
        ISessionStore sessions,
        IRealtimeChatExportReader chatExport,
        IAttachmentMetadataStore attachmentMeta,
        IServiceScopeFactory scopeFactory,
        string jobId,
        long userId,
        DataExportStorageOptions opts,
        string leaseOwner,
        string leaseToken,
        int leaseSeconds,
        CancellationToken cancellationToken,
        DataExportStagingBudget? stagingBudget = null)
    {
        DataExportStagingBudget.Lease? stagingLease = null;
        try
        {
            if (stagingBudget is not null)
            {
                stagingLease = await stagingBudget.ReserveAsync(
                        GetStagingReservationBytes(opts),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await ProcessJobCoreAsync(
                    db,
                    blob,
                    sessions,
                    chatExport,
                    attachmentMeta,
                    scopeFactory,
                    jobId,
                    userId,
                    opts,
                    leaseOwner,
                    leaseToken,
                    leaseSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (stagingLease is not null)
                await stagingLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The final export file and the messages/receipts/attachments side files
    // can coexist while the chat section is assembled.  Reserve the complete
    // worst-case aggregate, rather than the old two-file estimate.  The
    // options validator requires StagingMaxBytes to be at least this value;
    // returning the full value here also makes an invalid programmatic options
    // instance fail admission instead of silently overcommitting the quota.
    internal const int MaxConcurrentStagingFiles = 4;

    internal static long GetStagingReservationBytes(DataExportStorageOptions opts)
    {
        var maxExportBytes = Math.Max(1, opts.MaxExportBytes);
        var estimatedPeak = maxExportBytes > long.MaxValue / MaxConcurrentStagingFiles
            ? long.MaxValue
            : maxExportBytes * MaxConcurrentStagingFiles;
        return estimatedPeak;
    }

    private static async Task ProcessJobCoreAsync(
        UserDbContext db,
        IDataExportBlobStore blob,
        ISessionStore sessions,
        IRealtimeChatExportReader chatExport,
        IAttachmentMetadataStore attachmentMeta,
        IServiceScopeFactory scopeFactory,
        string jobId,
        long userId,
        DataExportStorageOptions opts,
        string leaseOwner,
        string leaseToken,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        await ThrowIfCancellationRequestedAsync(db, jobId, cancellationToken)
            .ConfigureAwait(false);
        // Lease renewal is owned by LeasedJobExecutor. This method performs
        // one initial ownership check, then only does export work and the
        // fenced Ready publication.
        await RenewLeaseAsync(
                db,
                jobId,
                leaseOwner,
                leaseToken,
                Math.Max(30, leaseSeconds),
                cancellationToken)
            .ConfigureAwait(false);

            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
                       ?? throw new InvalidOperationException("用户不存在");

            var events = await db.SecurityEvents.AsNoTracking()
                .Where(e => e.UserId == userId).OrderByDescending(e => e.Id).Take(2000)
                .Select(e => new { e.Id, e.EventType, e.DeviceId, e.SessionId, e.ClientIp, e.Location, e.Detail, e.CreatedAt })
                .ToListAsync(cancellationToken);
            var notifications = await db.InAppNotifications.AsNoTracking()
                .Where(n => n.UserId == userId).OrderByDescending(n => n.Id).Take(2000)
                .Select(n => new { n.Id, n.Type, n.Title, n.Body, n.IsRead, n.CreatedAt })
                .ToListAsync(cancellationToken);

            var reports = await db.UserReports.AsNoTracking()
                .Where(r => r.ReporterId == userId || r.TargetUserId == userId)
                .OrderByDescending(r => r.Id).Take(500)
                .Select(r => new
                {
                    r.Id,
                    r.ReporterId,
                    r.TargetType,
                    r.TargetUserId,
                    r.TargetMessageId,
                    r.Reason,
                    r.Status,
                    r.CreatedAt,
                })
                .ToListAsync(cancellationToken);
            var devices = await db.TrustedDevices.AsNoTracking()
                .Where(d => d.UserId == userId)
                .Select(d => new { d.Id, d.DeviceIdHint, d.Label, d.ClientIp, d.TrustedAt, d.ExpiresAt, d.RevokedAt })
                .ToListAsync(cancellationToken);
            var sessionList = await sessions.ListSessionsAsync(userId.ToString(), cancellationToken);

            // A lease-scoped candidate is never shared with a later owner of the same job.
            // It becomes visible to clients only after the fenced Ready transition below succeeds.
            // All export objects use a dedicated candidate prefix. The DB row
            // is still the publication authority, while bucket Lifecycle can
            // reclaim an object whose process died before the fenced Ready
            // update or whose durable tombstone could not be written.
            var objectKey = $"candidates/{userId}/{jobId}-{leaseToken}.json";
            var stagingRoot = GetStagingRoot(opts);
            var tempPath = Path.Combine(
                stagingRoot,
                $"chatapp-export-{jobId}-{leaseToken}.json");
            EnsureStagingQuota(opts, stagingRoot);
            var candidateWriteStarted = false;
            try
            {
                await using (var fs = new FileStream(
                                 tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 bufferSize: 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    // Enforce the per-user limit while the JSON is being
                    // produced. The post-write FileInfo check remains as a
                    // defense in depth, but it must not be the first point at
                    // which an oversized export is rejected.
                    var bounded = new MaxBytesWriteStream(
                        fs,
                        Math.Max(1, opts.MaxExportBytes));
                    var writer = new SequentialJsonObjectWriter(bounded);
                    await writer.StartAsync(cancellationToken).ConfigureAwait(false);
                    await writer.WritePropertyAsync("exportedAt", DateTimeOffset.UtcNow, cancellationToken)
                        .ConfigureAwait(false);
                    await writer.WritePropertyAsync("profile", new
                    {
                        user.Id,
                        user.UserName,
                        user.Email,
                        user.Signature,
                        user.Region,
                        user.CreatedDate,
                        user.AvatarUrl,
                        user.PhoneNumber,
                    }, cancellationToken).ConfigureAwait(false);
                    await writer.WritePropertyAsync("securityEvents", events, cancellationToken).ConfigureAwait(false);
                    await writer.WritePropertyAsync("notifications", notifications, cancellationToken).ConfigureAwait(false);
                    await writer.WritePropertyAsync("reports", reports, cancellationToken).ConfigureAwait(false);
                    await writer.WritePropertyAsync("trustedDevices", devices, cancellationToken).ConfigureAwait(false);
                    await writer.WritePropertyAsync("sessions", sessionList.Select(s => new
                    {
                        s.DeviceId,
                        s.ClientIp,
                        s.LoginAt,
                        s.LastActiveAt,
                        s.DeviceName,
                        s.SessionId,
                    }), cancellationToken).ConfigureAwait(false);

                    await WriteChatExportAsync(writer, chatExport, attachmentMeta, userId, opts, cancellationToken)
                        .ConfigureAwait(false);
                    await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
                }

                var finalSize = new FileInfo(tempPath).Length;
                if (finalSize > Math.Max(1, opts.MaxExportBytes))
                    throw new InvalidOperationException("导出结果超过单用户大小上限");
                EnsureStagingQuota(opts, stagingRoot);

                await using (var read = new FileStream(
                                 tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                 bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    candidateWriteStarted = true;
                    await blob.WriteAsync(objectKey, read, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                if (candidateWriteStarted)
                    await DiscardUnpublishedCandidateAsync(
                            db, blob, jobId, leaseOwner, leaseToken, objectKey)
                        .ConfigureAwait(false);
                throw;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                    RefreshStagingBytes(stagingRoot);
                }
                catch { /* best effort */ }
            }

            var readyAt = DateTimeOffset.UtcNow;
            await ThrowIfCancellationRequestedAsync(db, jobId, cancellationToken)
                .ConfigureAwait(false);
            var ttlHours = Math.Clamp(opts.JobTtlHours, 1, 168);
            // Only the current holder may publish its candidate key into the durable job row.
            int updated;
            try
            {
                updated = await db.DataExportJobs
                    .Where(j => j.Id == jobId
                        && j.LeaseOwner == leaseOwner
                        && j.LeaseToken == leaseToken
                        && j.Status == DataExportJobStatus.Processing)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(j => j.Status, DataExportJobStatus.Ready)
                            .SetProperty(j => j.ReadyAt, readyAt)
                            .SetProperty(j => j.ExpiresAt, readyAt.AddHours(ttlHours))
                            .SetProperty(j => j.ObjectKey, objectKey)
                            .SetProperty(j => j.Error, (string?)null)
                            .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                            .SetProperty(j => j.LeaseOwner, (string?)null)
                            .SetProperty(j => j.LeaseToken, (string?)null),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                await DiscardUnpublishedCandidateAsync(
                        db, blob, jobId, leaseOwner, leaseToken, objectKey)
                    .ConfigureAwait(false);
                throw;
            }

            if (updated == 0)
            {
                var cancellationRequested = await IsCancellationRequestedAsync(
                        db, jobId, cancellationToken)
                    .ConfigureAwait(false);
                await DiscardUnpublishedCandidateAsync(
                        db, blob, jobId, leaseOwner, leaseToken, objectKey)
                    .ConfigureAwait(false);
                if (cancellationRequested)
                    throw new DataExportCancellationRequestedException();
                throw new InvalidOperationException("导出完成但租约已易主，丢弃候选结果");
            }
    }

    internal static async Task MarkCancelledAsync(
        UserDbContext db,
        string jobId,
        string leaseOwner,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        await db.DataExportJobs
            .Where(j => j.Id == jobId
                        && (j.Status == DataExportJobStatus.Processing
                            || j.Status == DataExportJobStatus.CancelRequested)
                        && j.LeaseOwner == leaseOwner
                        && j.LeaseToken == leaseToken)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, DataExportJobStatus.Cancelled)
                    .SetProperty(j => j.Error, DataExportJobErrors.Cancelled)
                    .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(j => j.LeaseOwner, (string?)null)
                    .SetProperty(j => j.LeaseToken, (string?)null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task WriteChatExportAsync(
        SequentialJsonObjectWriter writer,
        IRealtimeChatExportReader chatExport,
        long userId,
        DataExportStorageOptions opts,
        CancellationToken cancellationToken)
        => await WriteChatExportAsync(
            writer, chatExport, UnavailableAttachmentMetadataStore.Instance, userId, opts, cancellationToken)
            .ConfigureAwait(false);

    internal static async Task WriteChatExportAsync(
        SequentialJsonObjectWriter writer,
        IRealtimeChatExportReader chatExport,
        IAttachmentMetadataStore attachmentMeta,
        long userId,
        DataExportStorageOptions opts,
        CancellationToken cancellationToken)
    {
        if (!opts.IncludeChatContent)
        {
            await writer.WritePropertyAsync("chatExport", new
            {
                status = "skipped",
                reason = "DataExport:IncludeChatContent=false",
            }, cancellationToken).ConfigureAwait(false);
            await WriteEmptyChatArraysAsync(writer, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!chatExport.IsAvailable)
        {
            await writer.WritePropertyAsync("chatExport", new
            {
                status = "unavailable",
                reason = chatExport.UnavailableReason,
            }, cancellationToken).ConfigureAwait(false);
            await WriteEmptyChatArraysAsync(writer, cancellationToken).ConfigureAwait(false);
            return;
        }

        var pageSize = Math.Clamp(opts.ChatExportPageSize, 1, 500);
        var maxMessages = Math.Max(1, opts.ChatExportMaxMessages);
        var maxAttachmentUrls = Math.Max(0, opts.ChatExportMaxAttachmentUrls);
        var urlScanMaxChars = Math.Max(0, opts.ChatExportUrlScanMaxContentChars);
        var seenAttachmentUrls = new HashSet<string>(StringComparer.Ordinal);
        var messageCount = 0;
        var receiptCount = 0;
        var attachmentCount = 0;
        var formalAttachmentCount = 0;
        var truncated = false;
        var attachmentUrlsCapped = false;
        var urlScanSkippedNote = false;

        var stagingRoot = GetStagingRoot(opts);
        EnsureStagingQuota(opts, stagingRoot);
        var receiptsPath = Path.Combine(stagingRoot, $"chatapp-export-rcpt-{Guid.NewGuid():N}.json");
            var messagesPath = Path.Combine(stagingRoot, $"chatapp-export-msg-{Guid.NewGuid():N}.json");
        var attachmentsPath = Path.Combine(stagingRoot, $"chatapp-export-att-{Guid.NewGuid():N}.json");
        try
        {
            await using (var messagesFs = new FileStream(
                             messagesPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                             bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var receiptsFs = new FileStream(
                             receiptsPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                             bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var attachmentsFs = new FileStream(
                             attachmentsPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                             bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var perSideFileLimit = Math.Max(1, opts.MaxExportBytes);
                var boundedMessages = new MaxBytesWriteStream(messagesFs, perSideFileLimit);
                var boundedReceipts = new MaxBytesWriteStream(receiptsFs, perSideFileLimit);
                var boundedAttachments = new MaxBytesWriteStream(attachmentsFs, perSideFileLimit);
                await using var receiptsWriter = new Utf8JsonWriter(boundedReceipts);
                await using var messagesWriter = new Utf8JsonWriter(boundedMessages);
                await using var attachmentsWriter = new Utf8JsonWriter(boundedAttachments);
                receiptsWriter.WriteStartArray();
                messagesWriter.WriteStartArray();
                attachmentsWriter.WriteStartArray();

                // Formal attachments first (DB rows), then legacy parser fallback de-duped by URL.
                if (attachmentMeta.IsAvailable)
                {
                    try
                    {
                        var formal = await attachmentMeta.ListForExportAsync(
                            userId,
                            maxRows: maxAttachmentUrls > 0 ? maxAttachmentUrls : 50_000,
                            cancellationToken).ConfigureAwait(false);
                        foreach (var row in formal)
                        {
                            if (maxAttachmentUrls > 0 && seenAttachmentUrls.Count >= maxAttachmentUrls)
                            {
                                attachmentUrlsCapped = true;
                                break;
                            }

                            var url = row.PublicUrl;
                            if (string.IsNullOrWhiteSpace(url))
                                url = row.ObjectKey;
                            if (string.IsNullOrWhiteSpace(url) || !seenAttachmentUrls.Add(url))
                                continue;

                            var item = new ChatExportAttachmentItem(
                                MessageId: row.MessageId ?? string.Empty,
                                ReceivedAtMs: row.BoundAtMs ?? row.ConfirmedAtMs ?? row.CreatedAtMs,
                                Url: url,
                                Name: row.OriginalName,
                                ContentType: row.ContentType,
                                SizeBytes: row.SizeBytes,
                                Source: "formal");
                            JsonSerializer.Serialize(attachmentsWriter, item);
                            attachmentCount++;
                            formalAttachmentCount++;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // Formal source optional; fall through to parser.
                    }
                }

                await foreach (var msg in chatExport.ReadMessagesAsync(userId, maxMessages + 1, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    if (messageCount >= maxMessages)
                    {
                        truncated = true;
                        break;
                    }

                    // 撤回 stub：正文置空；编辑/撤回字段加性导出，兼容旧客户端忽略未知字段。
                    var exportContent = msg.IsRecalled ? string.Empty : msg.Content;
                    JsonSerializer.Serialize(messagesWriter, new
                    {
                        msg.MessageId,
                        msg.ClientMessageId,
                        msg.SenderUserId,
                        msg.ReceiverUserId,
                        Content = exportContent,
                        msg.ReceivedAtMs,
                        msg.DeliveredAtMs,
                        msg.ReadAtMs,
                        msg.EditVersion,
                        msg.EditedAtMs,
                        msg.IsRecalled,
                        msg.RecalledAtMs,
                    });

                    // 回执写入侧流，避免在内存中缓冲全部 receipts。
                    if (msg.DeliveredAtMs is not null || msg.ReadAtMs is not null)
                    {
                        JsonSerializer.Serialize(receiptsWriter, new
                        {
                            msg.MessageId,
                            msg.DeliveredAtMs,
                            msg.ReadAtMs,
                        });
                        receiptCount++;
                    }

                    // 撤回消息无正文，跳过附件 URL 扫描。
                    var skipUrlScan = msg.IsRecalled
                                     || attachmentUrlsCapped
                                     || (urlScanMaxChars > 0 && exportContent.Length > urlScanMaxChars);
                    if (skipUrlScan && !msg.IsRecalled && !attachmentUrlsCapped
                        && exportContent.Length > urlScanMaxChars)
                        urlScanSkippedNote = true;

                    if (!attachmentUrlsCapped && !msg.IsRecalled)
                    {
                        foreach (var att in ChatExportAttachmentParser.Extract(
                                     msg.MessageId, msg.ReceivedAtMs, exportContent,
                                     urlScanMaxChars, skipUrlScan))
                        {
                            if (maxAttachmentUrls > 0 && seenAttachmentUrls.Count >= maxAttachmentUrls)
                            {
                                attachmentUrlsCapped = true;
                                break;
                            }

                            if (!seenAttachmentUrls.Add(att.Url))
                                continue;
                            JsonSerializer.Serialize(attachmentsWriter, att);
                            attachmentCount++;
                        }
                    }

                    messageCount++;
                    // 长导出期间周期性 flush，配合租约心跳避免缓冲过大。
                    if ((messageCount & 0x1FF) == 0)
                    {
                        await messagesWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                        await receiptsWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                        await attachmentsWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                messagesWriter.WriteEndArray();
                receiptsWriter.WriteEndArray();
                attachmentsWriter.WriteEndArray();
                await receiptsWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                await attachmentsWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                await messagesWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await writer.WriteRawJsonFilePropertyAsync(
                    "messages", messagesPath, cancellationToken, deleteSourceAfterCopy: true)
                .ConfigureAwait(false);
            await writer.WriteRawJsonFilePropertyAsync(
                    "receipts", receiptsPath, cancellationToken, deleteSourceAfterCopy: true)
                .ConfigureAwait(false);
            await writer.WriteRawJsonFilePropertyAsync(
                    "attachments", attachmentsPath, cancellationToken, deleteSourceAfterCopy: true)
                .ConfigureAwait(false);

            await writer.WritePropertyAsync("chatExport", new
            {
                status = truncated || attachmentUrlsCapped ? "truncated" : "ok",
                messageCount,
                receiptCount,
                attachmentCount,
                formalAttachmentCount,
                pageSize,
                maxMessages,
                truncated,
                attachmentUrlsCapped,
                urlScanSkipped = urlScanSkippedNote,
                note = attachmentUrlsCapped
                    ? "attachment URL dedupe set capped; further URL scan skipped"
                    : urlScanSkippedNote
                        ? "some message bodies exceeded urlScanMaxContentChars; URL scan skipped for those"
                        : (string?)null,
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(messagesPath)) File.Delete(messagesPath); } catch { /* best effort */ }
            try { if (File.Exists(receiptsPath)) File.Delete(receiptsPath); } catch { /* best effort */ }
            try { if (File.Exists(attachmentsPath)) File.Delete(attachmentsPath); } catch { /* best effort */ }
            RefreshStagingBytes(stagingRoot);
        }
    }

    private static async Task WriteEmptyChatArraysAsync(
        SequentialJsonObjectWriter writer,
        CancellationToken cancellationToken)
    {
        await writer.WritePropertyAsync("messages", Array.Empty<object>(), cancellationToken).ConfigureAwait(false);
        await writer.WritePropertyAsync("receipts", Array.Empty<object>(), cancellationToken).ConfigureAwait(false);
        await writer.WritePropertyAsync("attachments", Array.Empty<object>(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    internal static string GetStagingRoot(DataExportStorageOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.LocalRootPath))
            throw new InvalidOperationException("DataExport:LocalRootPath 不能为空");

        var root = Path.GetFullPath(opts.LocalRootPath);
        var staging = Path.Combine(root, ".staging");
        Directory.CreateDirectory(staging);
        RefreshStagingBytes(staging);
        return staging;
    }

    private static void EnsureStagingQuota(
        DataExportStorageOptions opts,
        string stagingRoot)
    {
        var bytes = RefreshStagingBytes(stagingRoot);
        if (bytes >= Math.Max(1, opts.StagingMaxBytes))
            throw new InvalidOperationException("导出 staging 磁盘配额已耗尽");
    }

    internal static long RefreshStagingBytes(string stagingRoot)
    {
        long bytes = 0;
        try
        {
            if (Directory.Exists(stagingRoot))
            {
                foreach (var path in Directory.EnumerateFiles(
                             stagingRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    try { bytes = checked(bytes + new FileInfo(path).Length); }
                    catch (FileNotFoundException) { }
                }
            }
        }
        catch (DirectoryNotFoundException) { }

        AuthSecurityMetrics.SetExportStagingBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// A candidate whose lease fence did not publish it must first become a
    /// durable PendingDelete tombstone. The delete remains best effort, but a
    /// failed delete is now discoverable and retryable by the export worker.
    /// </summary>
    private static async Task DiscardUnpublishedCandidateAsync(
        UserDbContext db,
        IDataExportBlobStore blob,
        string jobId,
        string leaseOwner,
        string leaseToken,
        string objectKey)
    {
        var tombstoneWritten = false;
        try
        {
            var tombstoned = await db.DataExportJobs
                .Where(j => j.Id == jobId
                            && (j.Status == DataExportJobStatus.Processing
                                || j.Status == DataExportJobStatus.CancelRequested)
                            && j.LeaseOwner == leaseOwner
                            && j.LeaseToken == leaseToken)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(j => j.Status, DataExportJobStatus.PendingDelete)
                        .SetProperty(j => j.ObjectKey, objectKey)
                        .SetProperty(j => j.Error, "unpublished_candidate_cleanup")
                        .SetProperty(j => j.ConsumedAt, DateTimeOffset.UtcNow)
                        .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                        .SetProperty(j => j.LeaseOwner, (string?)null)
                        .SetProperty(j => j.LeaseToken, (string?)null),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (tombstoned == 1)
            {
                tombstoneWritten = true;
                AuthSecurityMetrics.ExportPendingDeleteDelta(1);
            }
        }
        catch
        {
            // The command may have committed even though the connection
            // reported an error. Never delete an object after an ambiguous DB
            // result: the Ready publication could already reference it. The
            // candidates/ prefix Lifecycle rule is the safe eventual cleanup
            // path until a later reconciliation can write a tombstone.
            AuthSecurityMetrics.ExportBlobDelete("candidate_tombstone_failed");
            return;
        }

        // A zero-row fenced update means the lease is no longer ours. The
        // candidate is intentionally left to the prefix Lifecycle rule; this
        // avoids deleting a key whose publication outcome is ambiguous.
        if (!tombstoneWritten)
        {
            AuthSecurityMetrics.ExportBlobDelete("candidate_tombstone_not_owned");
            return;
        }

        try
        {
            await blob.DeleteAsync(objectKey, CancellationToken.None).ConfigureAwait(false);
            AuthSecurityMetrics.ExportBlobDelete("candidate_cleanup_success");
            await db.DataExportJobs
                .Where(j => j.Id == jobId
                            && j.Status == DataExportJobStatus.PendingDelete
                            && j.ObjectKey == objectKey)
                .ExecuteDeleteAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            AuthSecurityMetrics.ExportBlobDelete("candidate_cleanup_failed");
        }
    }

    private static async Task ThrowIfCancellationRequestedAsync(
        UserDbContext db,
        string jobId,
        CancellationToken cancellationToken)
    {
        if (await IsCancellationRequestedAsync(db, jobId, cancellationToken).ConfigureAwait(false))
            throw new DataExportCancellationRequestedException();
    }

    private static Task<bool> IsCancellationRequestedAsync(
        UserDbContext db,
        string jobId,
        CancellationToken cancellationToken)
        => db.DataExportJobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => j.Status == DataExportJobStatus.CancelRequested
                         || j.Status == DataExportJobStatus.Cancelled)
            .SingleOrDefaultAsync(cancellationToken);


    private static async Task RenewLeaseAsync(
        UserDbContext db,
        string jobId,
        string leaseOwner,
        string leaseToken,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds);
        // P0-5.2：续租必须匹配 LeaseOwner + LeaseToken，确保只有当前持有者能延长租约。
        var n = await db.DataExportJobs
            .Where(j => j.Id == jobId
                && j.LeaseOwner == leaseOwner
                && j.LeaseToken == leaseToken
                && j.Status == DataExportJobStatus.Processing)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseUntil, until), cancellationToken)
            .ConfigureAwait(false);
        if (n == 0)
            throw new InvalidOperationException("导出租约已丢失");
    }

    /// <summary>
    /// Counts bytes written to the final per-job staging file without adding a
    /// second payload buffer. It is intentionally write-only and leaves the
    /// owned FileStream open; the surrounding using scope owns its lifetime.
    /// </summary>
    private sealed class MaxBytesWriteStream(Stream inner, long maxBytes) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken)
            => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWithinLimit(count);
            inner.Write(buffer, offset, count);
            _written += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWithinLimit(buffer.Length);
            inner.Write(buffer);
            _written += buffer.Length;
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            EnsureWithinLimit(count);
            return WriteArrayAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureWithinLimit(buffer.Length);
            return WriteMemoryAsync(buffer, cancellationToken);
        }

        private async Task WriteArrayAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer, offset, count, cancellationToken)
                .ConfigureAwait(false);
            _written += count;
        }

        private async ValueTask WriteMemoryAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _written += buffer.Length;
        }

        private void EnsureWithinLimit(int count)
        {
            if (count < 0 || _written > maxBytes - count)
                throw new IOException("导出结果超过单用户大小上限");
        }
    }
}
