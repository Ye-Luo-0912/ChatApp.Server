# Attachment ops (Admin)

Read-only operational visibility for attachment GC / scan / delete tombs. Auth: `[Authorize(Roles = "Admin")]`.

## Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/admin/ops/attachment-orphans` | Confirmed unbound past age, Ticketed/Uploaded past age, Scanning past `StuckScanningMinutes` |
| GET | `/api/admin/ops/attachment-delete-failures` | `T_AttachmentBlobDeleteJob` pending/high-attempt + worst samples |
| GET | `/api/admin/ops/attachment-scan-backlog` | `T_AttachmentScanJob` status counts + oldest ages + worst open |
| GET | `/api/admin/ops/attachments/{attachmentId}/scan-audits` | Durable engine/version/verdict/reason history for one attachment |
| GET | `/api/admin/ops/attachment-hints` | Active Confirmed/Bound size sum (Realtime metadata), download-ticket TTL note |
| POST | `/api/admin/ops/attachments/{attachmentId}/rescan` | Reset a scan job and enqueue a fresh scan |
| POST | `/api/admin/ops/attachments/{attachmentId}/delete` | Mark rejected and enqueue a durable blob delete |
| POST | `/api/admin/ops/attachments/{attachmentId}/release` | Manually confirm after review; writes an admin audit row |

## Thresholds (`AttachmentStorage`)

- Orphan age = `max(30, TicketMinutes * 4)` minutes (same as `AttachmentCleanupWorker` storage GC)
- Aged unbound sweeper: `AbandonedUnboundEnabled` (default true); age = `AbandonedUnboundAgeMinutes` or orphan age fallback; batch = `AbandonedUnboundBatchSize` (default 50). Marks Ticketed/Confirmed unbound → Abandoned and enqueues blob delete tombs **before** storage GC.
- `StuckScanningMinutes` (default 30)
- `OpsHighDeleteAttemptThreshold` (default 5)
- `OpsSampleLimit` (default 20, clamped 1–20)

## Upload quota contract

- `MaxUnconfirmedObjectsPerUser` (default 20) limits `Ticketed` / `Uploaded` / `Scanning` objects per uploader.
- `MaxStorageBytesPerUser` (default 5 GiB) covers `Ticketed` / `Uploaded` / `Scanning` / `Confirmed` / `Bound` metadata rows.
- A `Ticketed` row reserves one `MaxBytes` unit, not the client-declared length; this prevents direct S3 PUT from under-declaring its body to bypass the quota. The size is replaced with the actual value after upload.
- Presign uses a per-user PostgreSQL transaction advisory lock before it reads usage and inserts the metadata row. A denial never returns an upload URL: pending-object limit is `429 AttachmentPendingLimitExceeded`, storage quota is `409 AttachmentStorageQuotaExceeded`, and unavailable metadata is `503 AttachmentMetadataUnavailable`.
- The orchestration immediately cancels the Redis upload ticket on a denied or failed reservation. Lifecycle and aged-unbound cleanup remain the fallback for crashes after a successful reservation.

## Related metrics (meter `Infrastructure.Attachments`)

- `attachment.blob_delete` (`outcome`: success/failed/exhausted)
- `attachment.scan` (`outcome`: enqueued/…/dead_letter)
- `attachment.upload_reservation` (`outcome`: reserved/pending_limit/storage_limit/metadata_unavailable)
- `attachment.pending_delete` / `attachment.pending_scan` gauges

Account cleanup saga / inbox DLQ remain under `/api/admin/account-cleanup-saga`.

## S3 production contract

- Client uploads are written only to
  `attachments/{userId}/staging/{attachmentId}`. The scanner records the exact
  source ETag, and confirmation conditionally copies that version to the immutable
  `attachments/{userId}/confirmed/{attachmentId}` key before publishing the final
  key to realtime metadata. A later PUT to the staging URL therefore cannot mutate
  a confirmed attachment. The staging object is removed through the durable blob
  delete queue. MIME type is stored in object `Content-Type` and realtime metadata;
  file-name extensions are not used for finalization.
- S3 clients use the AWS SDK default credential chain. Inject an IAM role,
  workload identity, profile, or standard environment credentials; do not put
  access keys in `AttachmentStorage` configuration.
- Set `AttachmentStorage:S3SseMode` to `SSE-S3` or `SSE-KMS` (and provide
  `S3KmsKeyId` for KMS). The same contract applies to avatars and data exports.
- Apply [attachments-lifecycle.json](../deploy/s3/attachments-lifecycle.json) to
  the bucket. The presign response's `UploadHeaders` must be sent unchanged by
  the client; this includes the initial `chatapp-scan-state=unconfirmed` tag
  and SSE headers. `chatapp-scan-state=quarantine` is set after upload
  confirmation and before the background scan; the database delete tombstone
  remains the source of truth for rejected/abandoned objects.
- In a production environment set `AttachmentStorage:ScannerProvider=ClamAV`
  and configure the ClamAV endpoint. `DenyList` is only a development fallback;
  it is not a malware scanner.
