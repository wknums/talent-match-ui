# Data-model - 008-platform-mode-shift

## Entity: ScoringBatch

Purpose: Represents one platform-mode submission unit containing 1..K applications.

Primary fields:
- `BatchId` (UUID/string, PK)
- `JobId` (string, FK to Job)
- `PromptVersionId` (string)
- `ApplicationIdsJson` (JSON array of application IDs, ordered)
- `RunCount` (int > 0)
- `Status` (`pending|submitting|submitted|completed|failed|cancelling|cancelled`)
- `SubmissionId` (nullable string)
- `PollUrl` (nullable string)
- `Attempt` (int >= 0)
- `SubmittedAt` (nullable datetime)
- `LastPolledAt` (nullable datetime)
- `NextPollAt` (datetime)
- `LastError` (nullable text)
- `LeaseOwner` (nullable string)
- `LeasedUntil` (nullable datetime)
- `ResultJson` (nullable text)
- `CancelRequested` (bool)
- `CreatedAt`, `UpdatedAt` (datetime)

Validation rules:
- `Status=submitted` requires non-null `SubmissionId`.
- `RunCount` must be >= 1.
- `ApplicationIdsJson` must be valid non-empty JSON array.
- Lease acquisition must be conditional on unowned/expired lease.

Indexes:
- `(JobId)`
- `(Status, NextPollAt)`
- unique filtered index on `SubmissionId` where non-null.

State transitions:
- `pending -> submitted`
- `pending -> failed`
- `pending -> cancelled` (if cancel requested before submit)
- `submitted -> completed|failed|cancelled`
- `submitted -> submitted` (polling continuation with next poll update)

## Entity: ScoringJobProgress

Purpose: Job-level counters and progress rollup for platform-mode reconciliation.

Fields:
- `JobId` (string, PK)
- `TotalApps` (int)
- `BatchesPending` (int)
- `BatchesSubmitted` (int)
- `BatchesCompleted` (int)
- `BatchesFailed` (int)
- `AppsCompleted` (int)
- `AppsFailed` (int)
- `CancelRequested` (bool)
- `StartedAt`, `UpdatedAt` (datetime)

Validation:
- All counters must remain >= 0.
- Counters updated atomically with batch state transitions where possible.

## Entity: ApplicationDocument (extended)

Purpose: Stores document metadata and canonical byte-location metadata.

Added fields:
- `BlobUri` (nullable string up to 1024)
- `ContentSha256` (nullable 64-char hex)

Canonical bytes rules:
- If `BlobUri` is non-null, Azure Blob is canonical source.
- If `BlobUri` is null, DB blob content is canonical source.
- New writes must store bytes in exactly one location per row.

Validation:
- If `ContentSha256` set, must be lowercase hex length 64.
- `BlobUri` must be absolute https URI when present.

## Entity: Application (extended)

Added field:
- `BatchId` (nullable UUID/string)

Purpose:
- Associates an application with current/most recent platform batch for progress and tracing.

## Contract payload model: Platform submission request

Logical model:
- `batchId` string (matches `Idempotency-Key`)
- `jobId` string
- `promptVersionId` string
- `runCount` int
- `prompt` object (`kind`, `text`)
- `cvs[]` where each item includes `applicationId`, `documentId`, `fileName`, `mimeType`, `blobUri`, `sha256`
- `callbackUrl` null for MVP

Validation:
- `cvs[]` length >= 1 and must match `ApplicationIdsJson` cardinality.
- Each cv item must include non-empty `blobUri` and `sha256` in platform mode.

## Relationship summary

- One Job to many ScoringBatches.
- One Job to one ScoringJobProgress (platform-mode lifecycle).
- One ScoringBatch to many Applications (via `ApplicationIdsJson`; optionally mirrored by `Applications.BatchId`).
- One Application to many ApplicationDocuments.
- One ApplicationDocument optionally references one blob object via `BlobUri`.

