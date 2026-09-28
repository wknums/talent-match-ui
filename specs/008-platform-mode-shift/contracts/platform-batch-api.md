# Platform Batch API Contract - 008-platform-mode-shift

## Scope

This contract captures the external client-to-platform API surface required by feature 008.
It is derived from and must stay aligned with `platform-contract.md`.

## Base behavior

- Active only when `AWR_PLATFORM_API_ENDPOINT` differs from `AWR_SEQ_API_ENDPOINT`.
- Authentication headers remain unchanged from current AWReason auth modes.
- `Idempotency-Key` is required for submit.

## Endpoint: POST /assess/batch

Purpose:
- Submit one platform batch for async processing.

Required headers:
- `Content-Type: application/json`
- `Idempotency-Key: <batchId>`
- auth header (`Authorization` or `X-AWR-API-Key`)

Request body shape:
- `batchId` string
- `jobId` string
- `promptVersionId` string
- `runCount` number
- `prompt`: `{ kind: "inline", text: string }` (MVP)
- `cvs[]` with:
  - `applicationId`
  - `documentId`
  - `fileName`
  - `mimeType`
  - `blobUri`
  - `sha256`
- `callbackUrl: null` for MVP

Success responses:
- `202 Accepted` for newly queued submission.
- `200 OK` for idempotent replay with original `submissionId`.

Response body:
- `submissionId` string
- `status` string
- `pollUrl` string
- `estimatedCompletionSeconds` number (optional)

Error contract (minimum):
- `400` validation error
- `401/403` auth error
- `409` idempotency conflict
- `429` throttling with retry guidance
- `5xx` transient platform error

## Endpoint: GET /assess/batch/{submissionId}/status

Purpose:
- Poll submission status and retrieve terminal results.

Response fields:
- `submissionId`
- `status` in `queued|running|completed|failed|cancelled`
- `retryAfterSeconds` optional for non-terminal states
- `result` present when completed
- `error` present when failed

Completed `result` shape:
- `cvs[]` each with:
  - `applicationId`
  - `runs[]`
  - `aggregated`

## Endpoint: POST /assess/batch/{submissionId}/cancel

Purpose:
- Request cancellation for an in-flight platform submission.

Expected responses:
- `202` cancellation accepted (`cancelling`)
- `200` already cancelled/terminal cancelled
- `409` too late (already completed)

## Client conformance rules

1. Persist `submissionId` and `pollUrl` before treating submit as successful.
2. Use exponential backoff and platform retry hints for polling.
3. Reconcile partial completion safely during cancellation.
4. Keep per-CV run/aggregate mapping compatible with sequential finalization logic.

## Storage and security linkage

1. Blob-by-reference only for platform submission payloads.
2. Managed identity + RBAC required; SAS tokens not allowed.
3. Per-row canonical byte source:
   - `BlobUri` present: blob object canonical.
   - `BlobUri` absent: DB blob canonical.
