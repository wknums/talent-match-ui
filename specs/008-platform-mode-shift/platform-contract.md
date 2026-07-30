# Platform-Mode Contract (Client ↔ Platform ↔ Engine)

**Status:** Draft for implementation — platform repo not yet operational
**Owners:** Client (this repo), Platform (Service Bus + Durable Functions), Engine (scoring runtime)
**Applies when:** `AWR_PLATFORM_API_ENDPOINT` is set and differs from `AWR_SEQ_API_ENDPOINT`

This document is the single source of truth for the contract that the client (both
Stack A / Node and Stack B / .NET) uses against the platform endpoint when
platform-mode orchestration is active. Platform and engine teams build to this
contract. Sequential mode (`/assess/passthrough` against `AWR_SEQ_API_ENDPOINT`) is
unchanged and outside the scope of this document.

---

## 1. Architectural shift

When platform mode is enabled, orchestration (queueing, fan-out per CV, run
repetition, retries, durability) moves from the **client web process** to the
**platform**. The client becomes a thin submit / reconcile / report layer:

```
┌────────────────────┐    submit batch     ┌────────────────────────────┐
│ Client (Node/.NET) │ ──────────────────► │ Platform                   │
│ - SQL: batches +   │ ◄────────────────── │ - Service Bus              │
│   progress         │     poll status     │ - Durable Functions        │
│ - Reconciler loop  │                     │ - Calls Engine per CV/run  │
└────────────────────┘                     └────────────────────────────┘
        │                                              │
        │   write blob (RBAC, MI)         read blob (RBAC, MI)
        ▼                                              ▼
                ┌───────────────────────────┐
                │ Azure Blob Storage        │
                │ container: cv-uploads     │
                └───────────────────────────┘
```

Two invariants drive everything below:

1. **The client persists the platform's handles** (`submissionId`, `pollUrl`)
   before considering a submission successful. No in-memory orchestration state.
2. **Every submission is idempotent** via a client-generated `batchId`. The
   platform must dedupe on this key.

---

## 2. Storage / RBAC

### 2.1 Blob storage

CVs are transferred **by reference**, not inline. The constitution
(`.specify/memory/constitution.md`) forbids SAS tokens.

| Resource | Identity | Role |
|---|---|---|
| Client app (Node / .NET) | App's user-assigned MI | `Storage Blob Data Contributor` on `cv-uploads` container |
| Platform compute | Platform's MI | `Storage Blob Data Reader` on `cv-uploads` container |
| Engine compute | Engine's MI (if separate) | `Storage Blob Data Reader` on `cv-uploads` container |

Container layout (client controls path):

```
cv-uploads/
  {jobId}/{applicationId}/{documentId}.{ext}
```

Client also writes a sibling content hash (`.sha256`) so platform/engine can
verify integrity without re-reading the whole blob.

### 2.2 Local dev fallback

When `STORAGE_PROVIDER=local`, blobs remain inline in
`talentmatch.DocumentBlobs.Content` (current behaviour). Platform-mode tests
against a local emulator can use Azurite with MI emulation, OR sequential mode
is used for local dev.

`STORAGE_PROVIDER` keeps its constitution-defined meaning (`local` | `azuresql`)
and is not used to select Blob Storage. Blob usage in platform mode is
controlled by blob-specific settings:

- `AWR_BLOB_STORAGE_ACCOUNT` (required in platform mode)
- `AWR_BLOB_CONTAINER` (optional, defaults to `cv-uploads`)

For shared/cloud operation where both stacks use the same SQL system of record,
platform mode requires `STORAGE_PROVIDER=azuresql` plus blob settings above.

---

## 3. SQL schema (shared `talentmatch` schema, both stacks)

### 3.1 `talentmatch.ScoringBatches`

```sql
CREATE TABLE [talentmatch].[ScoringBatches] (
    BatchId             UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    JobId               NVARCHAR(64)     NOT NULL,
    PromptVersionId     NVARCHAR(64)     NOT NULL,
    ApplicationIdsJson  NVARCHAR(MAX)    NOT NULL,   -- ordered JSON array of appIds
    RunCount            INT              NOT NULL,
    Status              NVARCHAR(20)     NOT NULL,   -- pending|submitting|submitted|completed|failed|cancelling|cancelled
    SubmissionId        NVARCHAR(128)    NULL,       -- platform-returned id
    PollUrl             NVARCHAR(512)    NULL,
    Attempt             INT              NOT NULL DEFAULT 0,
    SubmittedAt         DATETIME2        NULL,
    LastPolledAt        DATETIME2        NULL,
    NextPollAt          DATETIME2        NULL,       -- when reconciler should next look
    LastError           NVARCHAR(MAX)    NULL,
    LeaseOwner          NVARCHAR(128)    NULL,       -- reconciler instance id
    LeasedUntil         DATETIME2        NULL,
    ResultJson          NVARCHAR(MAX)    NULL,       -- raw platform result, audit only
    CreatedAt           DATETIME2        NOT NULL,
    UpdatedAt           DATETIME2        NOT NULL,
    CancelRequested     BIT              NOT NULL DEFAULT 0
);

CREATE INDEX IX_ScoringBatches_JobId        ON [talentmatch].[ScoringBatches](JobId);
CREATE INDEX IX_ScoringBatches_Status_Next  ON [talentmatch].[ScoringBatches](Status, NextPollAt);
CREATE UNIQUE INDEX UX_ScoringBatches_SubmissionId
    ON [talentmatch].[ScoringBatches](SubmissionId) WHERE SubmissionId IS NOT NULL;
```

### 3.2 `talentmatch.ScoringJobProgress`

```sql
CREATE TABLE [talentmatch].[ScoringJobProgress] (
    JobId            NVARCHAR(64)   NOT NULL PRIMARY KEY,
    TotalApps        INT            NOT NULL,
    BatchesPending   INT            NOT NULL DEFAULT 0,
    BatchesSubmitted INT            NOT NULL DEFAULT 0,
    BatchesCompleted INT            NOT NULL DEFAULT 0,
    BatchesFailed    INT            NOT NULL DEFAULT 0,
    AppsCompleted    INT            NOT NULL DEFAULT 0,
    AppsFailed       INT            NOT NULL DEFAULT 0,
    CancelRequested  BIT            NOT NULL DEFAULT 0,
    StartedAt        DATETIME2      NOT NULL,
    UpdatedAt        DATETIME2      NOT NULL
);
```

### 3.3 New columns

```sql
ALTER TABLE [talentmatch].[ApplicationDocuments]
    ADD BlobUri       NVARCHAR(1024) NULL,
        ContentSha256 CHAR(64)       NULL;

ALTER TABLE [talentmatch].[Applications]
    ADD BatchId UNIQUEIDENTIFIER NULL;
```

`BlobUri` is the canonical URI for production; `DocumentBlobs.Content` remains
the canonical store for local dev.

Canonical document-bytes source is determined per row:

- If `BlobUri` is non-null, the canonical bytes are the Azure Blob object at
  that URI.
- If `BlobUri` is null, canonical bytes are inline DB content
  (`DocumentBlobs.Content` / legacy `ContentBase64`).

For new writes, bytes MUST be stored in exactly one location for a document row
(no duplicate byte storage across DB blob columns and Azure Blob objects).
Read paths MUST support both cases so records created in one API mode remain
usable when the other mode is active.

---

## 4. Wire contract

### 4.1 `POST /assess/batch` — submit

**Headers:**
- `Authorization: Bearer …` (Entra ID) or `X-AWR-API-Key: …` (apikey mode) — unchanged
- `Idempotency-Key: <batchId>` — **REQUIRED in platform mode** (NEW)
- `Content-Type: application/json` — note: NOT multipart in platform mode

**Body:**

```jsonc
{
  "batchId": "9b8f…",                 // same as Idempotency-Key
  "jobId": "job-123",
  "promptVersionId": "pv-456",
  "runCount": 3,
  "prompt": {
    "kind": "inline",                 // "inline" | "ref" (future)
    "text": "<resolved prompt md>"    // present when kind=inline
  },
  "cvs": [
    {
      "applicationId": "app-001",
      "documentId":    "doc-aaa",
      "fileName":      "alice.pdf",
      "mimeType":      "application/pdf",
      "blobUri":       "https://<sa>.blob.core.windows.net/cv-uploads/job-123/app-001/doc-aaa.pdf",
      "sha256":        "f3b1…"
    },
    { "...": "K cvs total, K∈[1,N]; client default 2, ceiling agreed with platform" }
  ],
  "callbackUrl": null                  // reserved, null for MVP (poll only)
}
```

**Response 202 Accepted:**

```jsonc
{
  "submissionId":            "sub-7e2…",
  "status":                  "queued",
  "pollUrl":                 "/assess/batch/sub-7e2…/status",
  "estimatedCompletionSeconds": 120
}
```

**Idempotency:** if the same `Idempotency-Key` is received within the dedup
window (≥ 7 days), the platform MUST return the original `submissionId` with
`200 OK` (not create a new submission).

**Errors:**

| HTTP | Meaning | Client behaviour |
|---|---|---|
| 400 | Bad request (validation) | Mark batch `failed`, push to DLQ |
| 401 / 403 | Auth | Mark batch `failed`, alert |
| 409 | Idempotency conflict (different body, same key) | Mark batch `failed`, log |
| 429 | Rate limit | Honour `Retry-After`, leave `pending`, reconciler retries |
| 5xx | Transient | Leave `pending`, reconciler retries with backoff |

### 4.2 `GET /assess/batch/{submissionId}/status` — poll

**Response 200 OK:**

```jsonc
{
  "submissionId": "sub-7e2…",
  "status":       "queued|running|completed|failed|cancelled",
  "progress":     { "cvsCompleted": 1, "cvsTotal": 2 },     // optional
  "estimatedCompletionSeconds": 30,                          // optional
  "retryAfterSeconds": 10,                                   // platform's suggested next-poll delay
  "result": {                                                // present only when status=completed
    "cvs": [
      {
        "applicationId": "app-001",
        "runs": [ { "runIndex": 1, "...": "engine response" }, "..." ],
        "aggregated": { "finalScore": 78.2, "variance": 6.1, "...": "..." }
      },
      "... per cv ..."
    ]
  },
  "error": { "code": "...", "message": "..." }               // present only when status=failed
}
```

Notes:
- **Per-CV result map** is mandatory in platform mode. The current single-CV
  response shape from sequential mode is wrapped per `applicationId`.
- `aggregated` block per CV uses the same shape the engine currently returns in
  sequential mode (`finalScore`, `variance`, `subScoreAverages`,
  `consolidatedRationale`, `finalDecision`, `mustHaveResult`).

### 4.3 `POST /assess/batch/{submissionId}/cancel` — cancel (NEW)

**Body:** empty
**Response 202 Accepted:** `{ "status": "cancelling" }`
**Response 200 OK:** `{ "status": "cancelled" }` (already terminal)
**Response 409 Conflict:** `{ "status": "completed" }` (too late)

The platform MUST stop scheduling further engine work for the submission. CVs
already in flight may finish; the client reconciles partial results.

### 4.4 Endpoints removed from platform mode

`POST /assess/passthrough` is **not** used in platform mode. It remains the
sequential-mode endpoint against `AWR_SEQ_API_ENDPOINT`.

---

## 5. Engine-side impact

Platform mode does not change the engine's request/response shape per CV.
It changes only how requests reach the engine:

- Sequential mode: client → engine directly (`/assess/passthrough`, multipart).
- Platform mode: client → platform → engine. Platform fans out one engine call
  per (CV × run), supplying the engine with bytes it has read from blob using
  its own MI. Engine continues to accept multipart `promptFile` + `cvFiles[]`
  with one CV per call.

**Engine contract additions:**

1. Engine MUST accept an optional `X-AWR-Trace-Id` header (platform-generated)
   and echo it in responses, so the client can correlate platform results back
   to engine telemetry. No-op in sequential mode.
2. Engine output schema for a single scoring run is frozen as-is. Any change to
   `subScores` / `mustHaveResult` / `recommendations` must be coordinated with
   client through this document.

No other engine-side changes are required for the MVP.

---

## 6. Reconciler behaviour (client side)

Both stacks run a reconciler. Stack A: in-process Node `setInterval`. Stack B:
`IHostedService` with a periodic timer. Both use a SQL row-lease so multiple
replicas don't double-poll the same batch.

Loop, every `AWR_PLATFORM_RECONCILE_INTERVAL_MS` (default 15 s):

```
1. Acquire lease on up to N batches:
     UPDATE TOP (N) ScoringBatches
       SET LeaseOwner=@me, LeasedUntil=DATEADD(SS, 60, SYSUTCDATETIME())
     WHERE Status IN ('pending','submitting','submitted')
       AND NextPollAt <= SYSUTCDATETIME()
       AND (LeaseOwner IS NULL OR LeasedUntil < SYSUTCDATETIME())
     OUTPUT inserted.*;

2. For each leased batch:
     if Status='pending':
        submit (POST /assess/batch)
        on success: set submitted + SubmissionId + PollUrl + NextPollAt
        on retryable error: increment Attempt, push NextPollAt out
        on terminal error: mark failed, DLQ

     if Status='submitted':
        GET pollUrl
        if status=completed: persist results, mark batch completed, update progress
        if status=failed:    mark failed, DLQ
        if status=cancelled: mark cancelled
        if status=running|queued:
          NextPollAt = now + max(retryAfterSeconds, exp-backoff capped 30s)

3. Release lease (set LeaseOwner=NULL, LeasedUntil=NULL).
```

The reconciler is the **only** writer of batch state transitions after `pending`.
Submission no longer happens inside HTTP request handlers.

---

## 7. Submission flow (client side)

When an operator triggers "Process job":

```
1. Compute applicationIds to process.
2. Chunk into batches of size K (AWR_PLATFORM_BATCH_SIZE, default 2).
3. For each chunk, in a single SQL transaction:
     INSERT ScoringBatches (BatchId=newid(), Status='pending', NextPollAt=now, ...);
     UPDATE Applications SET BatchId=@b, Status='Scoring' WHERE Id IN (@ids);
   Commit.
4. Update ScoringJobProgress (BatchesPending += 1, TotalApps += K).
5. Return immediately to the caller. The reconciler picks the batch up.
```

Nothing in the HTTP request handler talks to the platform.

---

## 8. Configuration (NEW env vars)

| Var | Default | Purpose |
|---|---|---|
| `AWR_PLATFORM_BATCH_SIZE` | `2` | CVs per submission (raise after platform proves stable) |
| `AWR_PLATFORM_RECONCILE_INTERVAL_MS` | `15000` | Reconciler tick |
| `AWR_PLATFORM_LEASE_SECONDS` | `60` | Reconciler row-lease TTL |
| `AWR_PLATFORM_MAX_INFLIGHT_PER_TICK` | `50` | Cap batches reconciled per tick per replica |
| `AWR_BLOB_STORAGE_ACCOUNT` | — | Storage account name for `cv-uploads` (required in platform mode) |
| `AWR_BLOB_CONTAINER` | `cv-uploads` | Container name |
| `STORAGE_PROVIDER` | `local` | `local` \| `azuresql` (constitution-defined meaning) |

`AWR_MAX_PARALLEL` is retained for sequential mode and is **ignored** in
platform mode. Throughput in platform mode is bounded by `AWR_PLATFORM_MAX_INFLIGHT_PER_TICK`
× reconciler replicas × tick frequency, not by an application-level worker pool.

Platform mode prerequisites in this repo:

1. `AWR_PLATFORM_API_ENDPOINT` differs from `AWR_SEQ_API_ENDPOINT`
2. `STORAGE_PROVIDER=azuresql`
3. `AWR_BLOB_STORAGE_ACCOUNT` is set (`AWR_BLOB_CONTAINER` optional)

---

## 9. HANDOFF — changes required outside this repo

### 9.1 Platform repo (NEW work)

| Item | Required for MVP |
|---|---|
| Implement `POST /assess/batch` (JSON, blob-by-ref) per §4.1 | Yes |
| Implement `GET /assess/batch/{id}/status` per §4.2 | Yes |
| Implement `POST /assess/batch/{id}/cancel` per §4.3 | Yes |
| Honour `Idempotency-Key` header with ≥7-day dedup window | Yes |
| Read CVs from blob using platform MI (no SAS) | Yes |
| Return per-CV `result.cvs[]` array, not single result | Yes |
| Forward `X-AWR-Trace-Id` to engine | Yes |
| Webhook callback support (§4.1 `callbackUrl`) | No, reserved |

### 9.2 Engine repo (LOW impact)

| Item | Required for MVP |
|---|---|
| Accept + echo `X-AWR-Trace-Id` | Yes |
| Confirm output schema (`subScores`, `mustHaveResult`, etc.) is frozen | Yes |
| No changes to `/assess/passthrough` shape | — |

### 9.3 Shared infra (Bicep / Terraform)

| Item | Owner |
|---|---|
| `cv-uploads` blob container on existing storage account | Client repo (infra/) |
| Role assignment: client MI → `Storage Blob Data Contributor` on container | Client repo |
| Role assignment: platform MI → `Storage Blob Data Reader` on container | Platform repo |
| Role assignment: engine MI → `Storage Blob Data Reader` on container (if engine reads blob) | Platform/Engine |

---

## 10. Open questions for platform team

1. What is the platform's maximum supported `cvs[]` length per submission? We
   default K=2 but want a ceiling to validate against. (e.g. 100? 1000?)
2. Idempotency dedup window — confirm ≥7 days. If shorter, client may resubmit
   after restart of a long-running operator session.
3. Retry-After semantics — does the platform return seconds (integer) or HTTP
   date? Client will implement seconds for MVP.
4. Cancellation latency SLA — how quickly after `POST /cancel` does the platform
   stop scheduling new engine calls?
5. Authentication — does the platform support both `apikey` and Entra modes
   that the client uses today? Confirm header names match
   (`X-AWR-API-Key` / `Authorization`).
6. Result envelope when a batch partially fails (some CVs ok, some failed) —
   confirm `status=completed` with per-CV error fields, OR `status=failed` for
   the whole batch.

These should be answered before platform implementation begins.

---

## 11. Out of scope for MVP

- Webhooks / push-based completion
- K>2 batch sizes (contract supports them; default stays at 2 until validated)
- Streaming partial results during a long-running batch
- Multi-tenant batch isolation beyond what auth already provides
- Engine response schema changes
