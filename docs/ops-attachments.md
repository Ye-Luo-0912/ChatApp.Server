# Attachment ops (Admin)

Read-only operational visibility for attachment GC / scan / delete tombs. Auth: `[Authorize(Roles = "Admin")]`.

## Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/admin/ops/attachment-orphans` | Confirmed unbound past age, Ticketed/Uploaded past age, Scanning past `StuckScanningMinutes` |
| GET | `/api/admin/ops/attachment-delete-failures` | `T_AttachmentBlobDeleteJob` pending/high-attempt + worst samples |
| GET | `/api/admin/ops/attachment-scan-backlog` | `T_AttachmentScanJob` status counts + oldest ages + worst open |
| GET | `/api/admin/ops/attachment-hints` | Active Confirmed/Bound size sum (Realtime metadata), download-ticket TTL note |

## Thresholds (`AttachmentStorage`)

- Orphan age = `max(30, TicketMinutes * 4)` minutes (same as `AttachmentCleanupWorker` storage GC)
- Aged unbound sweeper: `AbandonedUnboundEnabled` (default true); age = `AbandonedUnboundAgeMinutes` or orphan age fallback; batch = `AbandonedUnboundBatchSize` (default 50). Marks Ticketed/Confirmed unbound → Abandoned and enqueues blob delete tombs **before** storage GC.
- `StuckScanningMinutes` (default 30)
- `OpsHighDeleteAttemptThreshold` (default 5)
- `OpsSampleLimit` (default 20, clamped 1–20)

## Related metrics (meter `Infrastructure.Attachments`)

- `attachment.blob_delete` (`outcome`: success/failed/exhausted)
- `attachment.scan` (`outcome`: enqueued/…/dead_letter)
- `attachment.pending_delete` / `attachment.pending_scan` gauges

Account cleanup saga / inbox DLQ remain under `/api/admin/account-cleanup-saga`.
