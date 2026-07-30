# Quickstart - 008-platform-mode-shift

## Purpose

Verify platform-mode orchestration behavior and storage semantics for feature 008.

## Prerequisites

1. Repository built for both stacks (or target stack under test).
2. Shared database configured (`STORAGE_PROVIDER=azuresql` for platform mode).
3. Platform endpoint reachable and distinct from sequential endpoint.
4. Blob account accessible via managed identity RBAC.

## Required configuration

Set these environment variables for platform-mode validation:

- `AWR_SEQ_API_ENDPOINT=<sequential-endpoint>`
- `AWR_PLATFORM_API_ENDPOINT=<platform-endpoint>` (must differ from sequential)
- `STORAGE_PROVIDER=azuresql`
- `AWR_BLOB_STORAGE_ACCOUNT=<storage-account-name>`
- `AWR_BLOB_CONTAINER=cv-uploads` (optional, defaults to this value)

Optional tuning:

- `AWR_PLATFORM_BATCH_SIZE=2`
- `AWR_PLATFORM_RECONCILE_INTERVAL_MS=15000`
- `AWR_PLATFORM_LEASE_SECONDS=60`
- `AWR_PLATFORM_MAX_INFLIGHT_PER_TICK=50`

## Validation checklist

### 1) Mode detection

1. Start application.
2. Confirm logs indicate platform mode (not sequential) when endpoints differ.

Expected:
- Platform-mode submission/reconciler paths are active.

### 2) Batch enqueue and submit

1. Trigger job processing for a job with multiple applications.
2. Check DB rows in `ScoringBatches` and `ScoringJobProgress`.

Expected:
- New `ScoringBatches` rows created with `pending` state then transition to `submitted`.
- `submissionId` and `pollUrl` persisted.

### 3) Cross-mode byte compatibility

Case A (BlobUri present):
1. Ensure an application document has `BlobUri` + `ContentSha256`.
2. Run sequential-mode scoring path against same record.

Expected:
- Document bytes resolved from blob and scoring succeeds.

Case B (BlobUri absent, DB content present):
1. Use legacy DB-inline record.
2. Run platform-mode submission.

Expected:
- Platform path can upload/read and persist metadata without breaking legacy record usage.

### 4) No duplicate byte storage for new writes

1. Create a new document row in platform mode.
2. Inspect row/storage behavior.

Expected:
- Exactly one canonical byte location per new row (blob or DB, not both duplicates).

### 5) Cancellation

1. Start a long-running job in platform mode.
2. Invoke cancellation endpoint.

Expected:
- Batch states converge to `cancelled` or terminal completed/failed per in-flight work.
- Progress counters reconcile without negative values.

### 6) Misconfiguration fail-fast

1. Keep platform endpoint configured but unset blob account.
2. Trigger job processing.

Expected:
- Explicit error indicates missing blob account precondition.

## Regression checks

1. With platform endpoint unset or equal to sequential, sequential mode still works.
2. Local mode (`STORAGE_PROVIDER=local`) remains functional for sequential processing.
3. No SAS token usage is introduced in config or runtime behavior.

