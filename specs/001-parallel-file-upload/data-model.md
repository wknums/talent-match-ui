# Data Model: Optional Parallel File Uploads

**Feature**: `001-parallel-file-upload`  
**Storage targets**: SQLite and Azure SQL, shared by Stack A and Stack B

## Conventions

- IDs are UUID strings using the repository's existing representation.
- Timestamps are UTC ISO-8601 instants in API contracts and provider-appropriate UTC
  values in persistence.
- Byte limits and file sizes are whole raw-byte counts. Defaults use binary units:
  `4 MiB = 4,194,304` and `100 MiB = 104,857,600`.
- Status values use the wire values defined here in both stacks.
- Every mutable row has an integer `ConcurrencyVersion`; successful updates increment it.
- Every state-changing operation emits an immutable existing `ProcessingEvent` with
  actor, timestamp, correlation ID, entity type/id, previous/new state where applicable,
  attempt, and safe reason metadata.

## Entity: UploadSettings

One current administrator-managed record for the optional path.

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `Id` | string | yes | Constant key `optional-file-upload`; primary key |
| `FileConcurrency` | integer | yes | Positive whole number; default `4` when the row is absent |
| `MaxIndividualFileBytes` | integer/long | yes | Positive whole byte count; default `4,194,304` |
| `MaxInFlightBytes` | integer/long | yes | Positive whole byte count; must be `>= MaxIndividualFileBytes`; default `104,857,600` |
| `ConcurrencyVersion` | integer | yes | Starts at `1`; optimistic concurrency token |
| `CreatedAt` | instant | yes | First persisted save |
| `CreatedBy` | string | yes | Existing authenticated global-admin actor ID |
| `UpdatedAt` | instant | yes | Last valid save |
| `UpdatedBy` | string | yes | Existing authenticated global-admin actor ID |

### Validation

- A missing row is not an error; reads materialize defaults without persisting them.
- Invalid values reject the entire update and retain the previous valid record.
- There is no runner/worker field because no independent upload-runner pool exists.
- Only the existing global `admin` role can read or update this entity.

## Entity: UploadSession

A durable intention by one authenticated user to upload selected file occurrences to one
job through the optional path.

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `Id` | string | yes | UUID primary key |
| `JobId` | string | yes | FK to existing `Jobs`; immutable |
| `OwnerActorId` | string | yes | Authenticated user/object ID; immutable |
| `OwnerDisplayName` | string | no | Snapshot for audit/display only |
| `AllowDuplicates` | boolean | yes | Snapshot from dialog; immutable |
| `Status` | enum | yes | `active` or `completed` |
| `FileConcurrency` | integer | yes | Validated settings snapshot; positive |
| `MaxIndividualFileBytes` | integer/long | yes | Validated settings snapshot; positive |
| `MaxInFlightBytes` | integer/long | yes | Validated settings snapshot; `>= MaxIndividualFileBytes` |
| `TotalItemCount` | integer | yes | Number of selected occurrences; `> 0` |
| `WaitingCount` | integer | yes | Items in `waiting` or `throttled` |
| `ActiveCount` | integer | yes | Items in `uploading` or `retrying` |
| `SucceededCount` | integer | yes | Items in `succeeded` |
| `SkippedCount` | integer | yes | Items in `skipped_duplicate` |
| `FailedCount` | integer | yes | Items in `failed` |
| `InterruptedCount` | integer | yes | Items in `interrupted` |
| `TerminalItemCount` | integer | yes | Succeeded + skipped + failed + interrupted |
| `CorrelationId` | string | yes | Stable session correlation ID |
| `LastHeartbeatAt` | instant | yes | Updated while the originating tab owns active work |
| `CreatedAt` | instant | yes | Before any file content transfer |
| `StartedAt` | instant | no | First item attempt accepted |
| `CompletedAt` | instant | no | Set when all items are terminal |
| `ConcurrencyVersion` | integer | yes | Optimistic concurrency token |

### Invariants

- One session belongs to exactly one job and one owner.
- Settings snapshot values never change after creation.
- Session creation locks or version-checks the singleton settings boundary in the same
  transaction as the session insert. A missing row participates as concurrency version
  `0` with the documented defaults, so a concurrent first settings save cannot produce
  a stale default snapshot.
- `TotalItemCount` equals the number of related UploadItems.
- All aggregate counts are non-negative and their category sum equals
  `TotalItemCount`.
- `Status = completed` iff `TerminalItemCount = TotalItemCount`; then `CompletedAt` is
  required.
- There is no session when every selected occurrence fails pre-transfer eligibility.
  The create request returns a field/item-specific validation response instead.
- Owner access is additionally constrained by the existing job authorization boundary.

## Entity: UploadItem

One selected file occurrence. Matching file content selected twice produces two items
with different occurrence keys.

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `Id` | string | yes | UUID primary key |
| `SessionId` | string | yes | FK to UploadSession; cascade delete is not exposed through this feature |
| `OccurrenceKey` | string | yes | Client-generated UUID; unique with SessionId; immutable idempotency identity |
| `Ordinal` | integer | yes | Zero-based selection order; unique with SessionId |
| `FileName` | string | yes | Sanitized display name; immutable |
| `MimeType` | string | yes | Normalized supported MIME; immutable after creation |
| `RawSizeBytes` | integer/long | yes | Client-declared raw length; non-negative; server verifies actual length |
| `Status` | enum | yes | Exact values listed below |
| `AttemptCount` | integer | yes | Starts `0`; maximum `4` |
| `ContentFingerprint` | string | no | Server-computed lowercase SHA-256 after bytes arrive |
| `ApplicationId` | string | no | Nullable FK to existing Applications; unique; only `succeeded` may set it |
| `OutcomeCode` | string | no | Stable machine-readable reason, e.g. `duplicate_existing`, `size_limit`, `unsupported_type`, `retry_exhausted`, `tab_interrupted` |
| `OutcomeMessage` | string | no | Safe, actionable user explanation; required for skipped/failed/interrupted |
| `LastHttpStatus` | integer | no | Last attempt response classification; never file content |
| `LastAttemptAt` | instant | no | Set when an attempt is accepted |
| `NextRetryAt` | instant | no | Present while delayed in `retrying` |
| `CreatedAt` | instant | yes | Same transaction as session creation |
| `UpdatedAt` | instant | yes | Last durable transition |
| `CompletedAt` | instant | no | Required for a terminal state |
| `ConcurrencyVersion` | integer | yes | Optimistic concurrency token |

### Item status enum

Wire values:

- `waiting`
- `throttled`
- `uploading`
- `retrying`
- `succeeded`
- `skipped_duplicate`
- `failed`
- `interrupted`

Display text may render `skipped_duplicate` as **Skipped as duplicate**, but storage and
contracts use the stable wire value.

### Keys and indexes

- Primary key: `Id`
- Unique: `(SessionId, OccurrenceKey)`
- Unique: `(SessionId, Ordinal)`
- Unique filtered/nullable association: `ApplicationId` when non-null
- Query index: `(SessionId, Status, Ordinal)`
- Reconciliation index: `(Status, UpdatedAt)`
- Duplicate lookup support: `(SessionId, ContentFingerprint)` plus the existing
  application-document fingerprint lookup for the job

### Validation

- Supported optional-path types are PDF, Markdown, DOCX, TXT, JPG, and PNG using the
  canonical MIME mapping in the API contract.
- At session creation, metadata-known invalid items receive a file-specific validation
  outcome and do not transfer. A session is created only if at least one occurrence is
  eligible.
- At content receipt, actual raw byte length and normalized MIME are checked again
  against the session snapshot. Exactly equal to `MaxIndividualFileBytes` is valid.
- A metadata/content size mismatch is a permanent `failed` validation outcome.
- `AttemptCount` increments once per server-accepted content attempt and cannot exceed 4.
- Browser transport attempts are counted independently because a request can fail before
  the content endpoint accepts it. After exactly four such attempts, the client may use
  the validated status operation to persist `failed` with `retry_exhausted` and a
  transport-attempt count of `4`; this does not fabricate a server `AttemptCount`.
- `ApplicationId` is write-once. Any repeated request for the same terminal item returns
  the recorded outcome.

## Existing entity: Application

No columns or state meanings change.

- A successful UploadItem has zero-or-one to exactly-one Application.
- The Application is created through the same existing application/document persistence
  and becomes `Queued` through the same publication boundary used today.
- Skipped, failed, and interrupted items never create an Application.
- Existing applications are not backfilled with UploadItems.
- Scoring ownership, leases, concurrency, rules, and results are unchanged.

## Existing entity: ApplicationDocument

No schema change is required.

- Optional completion stores the same filename, MIME, raw size, SHA-256 fingerprint,
  upload timestamp, and document content representation consumed by the current scoring
  path.
- `ContentFingerprint` on UploadItem equals the persisted document fingerprint for a
  successful item.

## Existing entity: ProcessingEvent

No schema change is required. Required event families:

| Event | Entity | Minimum safe details |
| --- | --- | --- |
| `upload-settings.updated` | UploadSettings | old/new limits, version |
| `upload-session.created` | UploadSession | job, item count, duplicate choice, settings snapshot |
| `upload-session.heartbeat` | UploadSession | active item count (coalescing is allowed only if the durable heartbeat update and event remain traceable) |
| `upload-item.state-changed` | UploadItem | previous/new status, attempt, reason code |
| `upload-item.completed` | UploadItem | terminal status, application ID if any, raw bytes, duration |
| `upload-session.completed` | UploadSession | final aggregate counts and duration |

Event details must never include source bytes, base64 content, authentication tokens, or
secrets.

## Relationships

```text
Jobs (existing) 1 ─────── * UploadSessions
Users/actors     1 ─────── * UploadSessions (logical ownership)
UploadSettings  1 ──snapshot── * UploadSessions
UploadSessions  1 ─────── * UploadItems
UploadItems     0..1 ───── 1 Applications (existing)
Applications    1 ─────── * ApplicationDocuments (existing)
UploadSettings / UploadSessions / UploadItems ───── * ProcessingEvents (logical audit)
```

The settings-to-session relationship is a value snapshot, not a foreign key. Deleting or
changing current settings cannot alter an active or historical session.

## State transitions

### UploadSession

```text
create eligible items
        │
        ▼
     active ───────────────► completed
              all items terminal
```

There is no canceled or paused session state in this feature.

### UploadItem

```text
waiting ───────────────► throttled ───────────────► uploading
   │                         │                         │
   ├─────────────────────────┘                         ├──► succeeded
   ├───────────────────────────────────────────────────├──► skipped_duplicate
   ├───────────────────────────────────────────────────├──► failed
   └───────────────────────────────────────────────────└──► interrupted

uploading ── transient failure ──► retrying ── next accepted attempt ──► uploading
retrying  ── attempts exhausted ─► failed
retrying  ── heartbeat expires ──► interrupted
```

Allowed transitions:

| From | To |
| --- | --- |
| `waiting` | `throttled`, `uploading`, `failed`, `interrupted` |
| `throttled` | `waiting`, `uploading`, `failed`, `interrupted` |
| `uploading` | `retrying`, `succeeded`, `skipped_duplicate`, `failed`, `interrupted` |
| `retrying` | `uploading`, `failed`, `interrupted` |
| terminal state | none; repeated submissions return the existing state |

`failed`, `succeeded`, `skipped_duplicate`, and `interrupted` are terminal. A file made
`interrupted` after source access is lost is not automatically resumed.

## Atomicity and concurrency

### Session creation

Create the session, all item rows, initial aggregate counts, and the session-created audit
event in one database transaction. No content endpoint accepts bytes until that
transaction commits.

### Item attempt

1. Lock/reload the item and parent session.
2. Verify owner/job authorization, occurrence key, nonterminal status, and attempt budget.
3. If terminal, return the stored result without mutation.
4. Increment attempt and transition to `uploading` with an audit event.
5. Validate and hash actual bytes.
6. For duplicates-disabled sessions, serialize the fingerprint decision on the parent
   session/job persistence boundary, check prior same-session fingerprints and existing
   job documents, and either record `skipped_duplicate` or continue.
7. Persist application/document content, set the item's write-once `ApplicationId`,
   publish that application to `Queued`, update item/session aggregates, and append
   audit events in the transactional unit supported by the provider.
8. Return the canonical stored item outcome.

SQLite uses its existing serialized write transaction convention; Azure SQL uses an
explicit transaction and update/key-range locking through the repository. The HTTP layer
does not implement locking or provider-specific SQL.

## Aggregate derivation

For an UploadSession:

- `WaitingCount = count(waiting) + count(throttled)`
- `ActiveCount = count(uploading) + count(retrying)`
- `SucceededCount = count(succeeded)`
- `SkippedCount = count(skipped_duplicate)`
- `FailedCount = count(failed)`
- `InterruptedCount = count(interrupted)`
- `TerminalItemCount = SucceededCount + SkippedCount + FailedCount + InterruptedCount`
- File progress percentage =
  `floor(TerminalItemCount / TotalItemCount * 100)`, with 100 only when completed

The user-facing surface also shows exact category counts; byte progress is not claimed
because the contract does not introduce chunked upload progress.

## Migration and backward compatibility

1. Add only the three new tables, constraints, and indexes.
2. Add EF Core mappings/migration/snapshot for Stack B.
3. Add equivalent idempotent SQLite and Azure SQL schema evolution for Stack A.
4. Add new repositories to the existing storage abstractions.
5. Do not alter or backfill current Applications, ApplicationDocuments, DocumentBlobs,
   or scoring tables.
6. Treat a missing UploadSettings row as valid defaults.
7. Keep the legacy upload endpoint and request/response contract unchanged.
8. Optional tables can remain after a UI rollback without affecting the default path.
