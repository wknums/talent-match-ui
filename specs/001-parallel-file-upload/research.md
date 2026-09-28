# Phase 0 Research: Optional Parallel File Uploads

**Feature**: `001-parallel-file-upload`  
**Repository**: `C:\code\awr-cv-match-client`  
**Completed**: 2026-09-22

## Research inventory

The repository was inspected for the current upload execution path, duplicate behavior,
storage and migration conventions, system-administrator authorization, browser state
lifetime, audit/observability, parser/validation limits, and test patterns in both stacks.
No external service or new dependency is needed.

## Decision 1: Preserve the bulk path as a hard compatibility boundary

**Decision**: Leave the current bulk path intact and branch in the dialog before calling
it. With optional mode off, Stack A continues through
`src/components/UploadApplicationsDialog.tsx` →
`src/lib/api-real.ts::uploadApplications` →
`server/routes/applications.ts` and Stack B continues through
`dotnet/src/Web.Client/Components/UploadApplications.razor` →
`ApiClient.UploadApplicationsAsync` →
`ApplicationsEndpoints` →
`UploadApplicationsCommandHandler`. The new mode calls only new session/item endpoints.

**Rationale**: FR-003 and FR-004 require unchanged submission, validation, progress,
duplicate handling, close behavior, result interpretation, and scoring behavior when the
control is off. The current implementations also differ materially: Stack A uses a
base64 JSON batch and creates queued applications directly, while Stack B uses multipart,
temporarily creates `Uploading` applications, publishes the whole batch, and then pulses
the existing scoring queue. Sharing/refactoring this code would create avoidable
regression risk.

**Alternatives considered**:

- Refactor both old and new modes through one scheduler: rejected because it changes the
  default path's timing, close behavior, partial-failure semantics, and request shape.
- Replace the old endpoint with a compatibility wrapper around individual uploads:
  rejected because a wrapper cannot preserve its existing batch transaction/publication
  behavior exactly.

## Decision 1A: Implement and gate Stack B before Stack A

**Decision**: Treat Stack B as the first implementation and validation target. Stack B is
the .NET/Blazor Clean Architecture path under `dotnet/`: Blazor component, typed
`ApiClient`, authorized ASP.NET Core endpoints, MediatR application handlers, Domain
contracts, and EF Core Infrastructure persistence. Complete that stack, including its
additive migration and optional-off/new-path regression coverage, then require a passing
Stack B gate before changing Stack A source or storage initialization. Stack A is the
React/TypeScript and Express path under `src/` and `server/`; it adopts the proven
contract and schema only after the gate.

**Rationale**: The repository constitution identifies Stack B as the Clean Architecture
target, and its current upload path already has explicit Application and Infrastructure
seams plus focused command/publication tests. Using it to prove the contract and additive
schema first avoids two implementations drifting in parallel. A blocking regression gate
also protects Stack B's multipart, all-or-none publication, and scoring-queue pulse before
Stack A maps the result into its materially different base64 JSON/direct-`Queued` path.

**Alternatives considered**:

- Implement both stacks in parallel: rejected because the user requires Stack B first
  and parallel work could independently reinterpret schema, status, and idempotency.
- Implement Stack A first because it is the prototype: rejected because it violates the
  required sequence and would make the target Clean Architecture stack adopt a
  prototype-led design.
- Share one implementation task across both stacks: rejected because it obscures the
  B-GATE dependency and risks refactoring their intentionally different legacy paths.

## Decision 2: Use an application-lifetime browser coordinator

**Decision**: Use a React context/provider mounted alongside the top-level application
shell in Stack A and a scoped Blazor WASM service registered in
`dotnet/src/Web.Client/Program.cs` in Stack B. Stack A retains its application-shell
status surface. Stack B keeps the coordinator at application lifetime but renders detailed
Upload activity only from Job Details, renders only an Active Uploads count on the
dashboard, and renders no upload status on System Configuration pages. The coordinator
owns local file handles and count/byte permits only for the life of the tab.

**Rationale**: Current dialog-local state is destroyed or disabled with dialog lifetime.
A provider/service at application scope survives internal navigation without requiring a
service worker or durable source-file cache. Blazor scoped services live for the WASM app
lifetime even though their detailed presentation is route-specific. Keeping presentation
out of `MainLayout.razor` avoids leaking job-specific details onto the dashboard and
administration pages while preserving active work. This exactly matches FR-009 through
FR-011 and FR-035 through FR-039.

**Alternatives considered**:

- Keep scheduling state in the upload dialog: rejected because closing the dialog would
  cancel or orphan work.
- Render Stack B Upload activity from `MainLayout.razor`: rejected because job-specific
  details would appear on unrelated dashboard and System Configuration pages.
- Service worker/background sync: rejected because durable access to local source files
  and continuation after tab close are explicitly excluded.
- Server-side upload runner: rejected because the server does not possess the local file
  before transfer and the repository has no independent upload runner model.

## Decision 3: Create the durable intention before content transfer

**Decision**: The optional client generates a stable UUID occurrence key for every
selected occurrence and posts a session plus all item metadata before posting any file
bytes. The server stores the actor, job, duplicate choice, settings snapshot, totals, and
items in one transaction. Only after successful creation does the coordinator schedule
individual item content requests.

**Rationale**: This directly satisfies FR-005, makes all selected occurrences observable
even if the tab closes immediately, and separates durable intent from non-durable local
file access. Occurrence identity, not filename or fingerprint, distinguishes
intentionally selected matching files.

**Alternatives considered**:

- Create each item when its upload begins: rejected because later queued files would
  disappear if the tab closes before their turn.
- Upload content in the session-creation request: rejected because it would recreate the
  current bulk path and violate the required create-before-transfer sequence.

## Decision 4: Enforce both limits in a deterministic client scheduler

**Decision**: The browser coordinator starts an item only when:

`activeItemCount < session.fileConcurrency` and
`activeRawBytes + item.rawSizeBytes <= session.maxInFlightBytes`.

It reserves both permits immediately before issuing the request and releases them in a
`finally` path. Valid work lacking either permit is marked `throttled`. Selection order
is the tie breaker so a large waiting item is not indefinitely bypassed by later small
items.

**Rationale**: Only the browser can control simultaneous transfers of local files.
Checking both conditions before each start gives testable enforcement of FR-006 through
FR-008. Raw file sizes are known before transfer and are independent from multipart or
base64 overhead.

**Alternatives considered**:

- A count-only promise pool: rejected because it cannot enforce the byte budget.
- Server-side HTTP rejection when capacity is exceeded: rejected because capacity
  saturation must wait rather than fail.
- Chunking files: rejected because resumable/chunked upload is not required and would add
  a new protocol.

## Decision 5: Use individual multipart content requests

**Decision**: Transfer one raw file per item using multipart form data, addressed by
session ID and item ID, with the occurrence key included as an idempotency value. Keep
the legacy Stack A base64 JSON batch unchanged.

**Rationale**: `server/index.ts` currently applies a 10 MB JSON body limit while Stack A
base64 expands raw data by roughly one third; the current dialog also advertises a
different 15 MB limit. An individual multipart request measures and validates raw bytes
directly and avoids coupling the new configurable limit to JSON encoding overhead.
Stack B already uses multipart successfully.

**Alternatives considered**:

- Reuse the Stack A base64 JSON shape: rejected because configured raw-byte limits would
  not align with encoded request limits.
- Raw `application/octet-stream`: viable but rejected in favor of multipart because the
  existing server/client conventions already handle filename and MIME metadata there.

## Decision 6: Make item completion the idempotency boundary

**Decision**: Enforce unique `(SessionId, OccurrenceKey)` and store at most one
`ApplicationId` on an item. The content handler locks/reloads the item in a transaction:
if it is terminal, it returns the recorded outcome; otherwise it validates content,
recomputes SHA-256, applies duplicate rules, persists one application/document, links the
item, publishes that application through the existing queued boundary, and commits the
terminal state. Concurrent or lost-response repeats resolve to the same item outcome.

**Rationale**: A content fingerprint cannot be the idempotency key because intentionally
selected duplicates are allowed to produce separate applications. The selected
occurrence is the unit named by FR-016 and FR-018. Database uniqueness and transactional
state, rather than an in-memory request cache, work across restarts and both providers.

**Alternatives considered**:

- Use fingerprint as the idempotency key: rejected because it collapses intentionally
  selected matching occurrences.
- Trust a client-only retry token: rejected because process restarts and concurrent
  requests could still create duplicate applications.
- Store an HTTP response cache only: rejected because it does not protect the underlying
  application write.

## Decision 7: Preserve duplicate semantics with an atomic fingerprint decision

**Decision**: With duplicates disabled, the optional completion transaction treats an
existing document fingerprint for the job or an earlier claimed fingerprint in the same
session as `skipped_duplicate`. A per-session fingerprint claim/transactional uniqueness
operation makes the first selected occurrence deterministic even when content requests
arrive concurrently. With duplicates enabled, the fingerprint check is bypassed but the
per-occurrence idempotency check remains.

**Rationale**: The existing Stack A route uses a request-local `Set` plus
`findDuplicateFingerprint`; Stack B uses a request-local `HashSet` plus
`FindDocumentByFingerprintAsync`. Parallel independent requests lose that batch-local
set, so the same rule must move to durable transactional state for the optional path.
The legacy code remains unchanged.

**Alternatives considered**:

- Depend only on the existing job lookup: rejected because two same-session uploads can
  race before either document commits.
- Serialize every optional upload: rejected because it defeats bounded parallelism.
- Add a global uniqueness constraint to application documents: rejected because it would
  forbid the existing duplicate-enabled behavior.

## Decision 8: Model exactly the specified states and bounded retries

**Decision**: Persist only `waiting`, `throttled`, `uploading`, `retrying`, `succeeded`,
`skipped_duplicate`, `failed`, and `interrupted`. Increment `AttemptCount` when the server
accepts an item attempt. Retry only network loss/timeouts, HTTP 408, 429, and 5xx
availability failures, with bounded backoff and at most three additional attempts
(four total). Do not retry type/size validation, authorization, not-found/job-scope,
duplicate, or other 4xx outcomes.

**Rationale**: These are the exact FR-013 states. Classifying failures avoids retrying
permanent input/permission errors while satisfying FR-015. The recorded attempt count and
terminal replay make retry behavior explainable.

**Alternatives considered**:

- Retry every failure: rejected because it wastes capacity and can repeat permanent
  authorization/validation failures.
- Unbounded exponential retry: rejected because the specification fixes three
  additional attempts.
- Add canceled/paused states: rejected because those topics are not in scope.

## Decision 9: Reconcile tab loss through heartbeat staleness

**Decision**: While work is active, the coordinator sends a session heartbeat. Retrieval
or reconciliation converts stale nonterminal items to `interrupted` after a documented
short lease window. The client attempts a best-effort final interruption update on page
unload but correctness does not depend on it. Completed/skipped/failed results remain
unchanged and visible.

**Rationale**: Browsers do not guarantee unload requests. A server-known heartbeat is the
only reliable way to distinguish an active originating tab from abandoned work without
storing source files. This meets FR-011, FR-012, and the reload scenario without claiming
automatic continuation.

**Alternatives considered**:

- Depend on `beforeunload` only: rejected because it is not reliable.
- Leave nonterminal items waiting forever: rejected because users must later see them as
  no longer uploading.
- Resume automatically after reload: rejected because file handles are intentionally not
  durable.

## Decision 10: Persist one settings record and omit runner count

**Decision**: Store one current upload-settings record with file concurrency, maximum
individual raw bytes, total in-flight raw bytes, update metadata, and optimistic version.
Use defaults 4, 4 MiB, and 100 MiB when absent. Do not add an upload runner/worker field.

**Rationale**: The feature assumptions say the repository has no separate upload-runner
capacity model. The browser coordinator is the runner and file concurrency is the only
count capacity. Adding another setting would be meaningless and violate YAGNI. Settings
validation requires positive whole-number concurrency, positive byte values, and total
bytes greater than or equal to individual bytes.

**Alternatives considered**:

- Add a default-1 runner setting preemptively: rejected because FR-026 is conditional and
  no independently operated runner exists.
- Environment variables only: rejected because authorized administrators must view and
  change settings at runtime.
- Read live settings before every file: rejected because active sessions must retain the
  values effective at creation.

## Decision 11: Reuse global-admin boundaries and canonical errors

**Decision**: Expose Settings only under **System Configuration** to the existing global
`admin` role, hide it from other users, and independently enforce that role on server
GET/PUT endpoints. Reuse Stack A `authorizationContext.globalRole === "admin"` /
`user.role === "admin"` checks and canonical correlation-aware authorization errors;
reuse Stack B authenticated endpoint filters with `User.IsInRole("admin")`.

**Rationale**: These are the existing boundaries used by extraction-instruction
administration in `server/routes/extraction-instructions.ts` and
`dotnet/src/Web.Server/Endpoints/ExtractionInstructionEndpoints.cs`. Organization
administrators are explicitly not system administrators.

**Alternatives considered**:

- Allow organization administrators: rejected by the specification assumption and
  FR-031.
- Rely on hidden navigation only: rejected because authorization must be server-side.

## Decision 12: Align optional validation without changing legacy validation

**Decision**: Define one optional-path validation contract shared by each stack's client
and server. Preserve the current application-dialog extension set (PDF, MD, DOCX, TXT,
JPG, PNG), normalize MIME from extension when browser MIME is empty, and validate the
actual raw length server-side against the session snapshot. Exactly-at-limit is accepted;
one byte over is failed individually. Do not modify the legacy limits or allowlists.

**Rationale**: Current boundaries are inconsistent: both dialogs advertise 15 MB and six
extensions; Stack A's legacy route allows only four MIME types and 2 MB; Stack A's JSON
body is capped at 10 MB; Stack B has dialog/stream limits but no equivalent endpoint
allowlist in `ApplicationsEndpoints`. FR-003 forbids repairing those legacy differences
as part of this feature, while FR-033/FR-034 and the planning constraint require the new
path to be internally aligned. Persisting the same document format keeps downstream
workers unchanged.

**Alternatives considered**:

- Change all existing limits to 4 MB: rejected because it changes the default path.
- Copy each legacy stack's mismatched validator into the optional path: rejected because
  the new cross-stack contract would be nondeterministic.
- Expand to additional image/document types: rejected because omitted types are outside
  the specification.

## Decision 13: Use polling/local events, not a new real-time transport

**Decision**: Update the originating tab's status surface immediately from coordinator
events and reconcile durable state with bounded polling (no slower than 2 seconds while
active, relaxed/stopped when terminal). Other reloads retrieve sessions/items through
GET endpoints. Do not add SignalR for this feature.

**Rationale**: The 2-second observation target can be met without introducing new
infrastructure, and the client already owns active transfer events. SignalR exists in the
broader architecture but is optional and not needed for a single originating tab.

**Alternatives considered**:

- SignalR push: rejected as unnecessary scope/operational complexity.
- Poll only every 30 seconds: rejected because it cannot meet SC-007.
- Keep status only in memory: rejected because terminal/server-known outcomes must be
  durable across reload.

## Decision 14: Reuse audit and structured logging conventions

**Decision**: Give each session a correlation ID and emit immutable `ProcessingEvent`
entries for settings changes, session creation, item attempt/state/terminal changes, and
session completion. Structured server logs include `sessionId`, `itemId`, `jobId`,
`attempt`, `status`, raw bytes, elapsed time, and reason code but never file content.
Aggregate telemetry reports queued/active/terminal counts, active bytes, retry count,
duration, and configured limits separately from scoring.

**Rationale**: The constitution requires every state change to leave a trace.
`server/services/audit.ts` and Stack B `ProcessingEvent` already provide actor, timestamp,
correlation ID, entity type/id, and safe details. Separate upload dimensions preserve the
required operational isolation from scoring.

**Alternatives considered**:

- Log terminal outcomes only: rejected because intermediate durable state changes would
  be unaudited.
- Log source payloads for diagnosis: rejected because document content is sensitive and
  unnecessary.

## Decision 15: Use additive, shared-schema migration

**Decision**: Add `UploadSettings`, `UploadSessions`, and `UploadItems` through an EF Core
code-first migration/model snapshot plus matching Stack A SQLite/Azure SQL idempotent
schema evolution. Add provider-neutral repository interfaces to the Stack A
`StorageProvider` and Stack B Domain. Do not change or backfill existing application,
document, blob, or scoring rows.

**Rationale**: Both stacks share SQLite/Azure SQL semantics. Additive tables make an
absent settings row naturally fall back to defaults and allow rollback of the opt-in UI
without affecting the established upload endpoint. Existing migrations already combine
EF migrations with Stack A compatibility DDL.

**Alternatives considered**:

- Store sessions in browser storage: rejected because outcomes would not be durable,
  authorized, or observable after a crash on another browser session.
- Embed upload metadata in Applications: rejected because items exist before and can
  terminate without an application.
- Backfill legacy uploads as sessions: rejected because no requirement needs historical
  reconstruction and the data lacks occurrence identity.

## Decision 16: Test the compatibility seam and concurrency deterministically

**Decision**: Add focused tests in the existing suites:

- Vitest/Testing Library and bUnit for opt-in default/reset, dialog branching, status UI,
  Settings validation, and admin visibility.
- Stack A integration and Stack B Application/Infrastructure/Web tests for schema,
  authorization, state transitions, idempotency, duplicate parity, settings snapshots,
  publication, and audit.
- Scheduler unit tests with deferred promises/streams and injectable time to assert exact
  count/byte maxima, waiting, retries, permit release, and isolated failures.
- Playwright browser coverage for closing the dialog, in-app navigation, aggregate
  visibility, and reload/interruption recovery.
- Explicit unchanged legacy tests asserting old clients/endpoints/handlers and scoring
  handoff are used when opt-in is off.

**Rationale**: Existing conventions already use Vitest integration tests for Stack A
upload duplicates, xUnit command/repository tests for Stack B publication and duplicate
behavior, bUnit for System Configuration navigation, and Playwright for cross-route
browser journeys. Deterministic scheduler tests avoid flaky timing-based concurrency
assertions.

**Alternatives considered**:

- End-to-end tests only: rejected because races, byte permits, and retry counts would be
  slow and flaky to isolate.
- Unit tests only: rejected because authorization, migrations, browser navigation, and
  actual request contracts cross layers.

## Resolved clarifications

| Topic | Resolution |
| --- | --- |
| Technology stacks | Implement contract parity in both Stack A and Stack B. |
| Upload runner count | Not modeled; no setting is added because no independent runner pool exists. |
| Queue lifetime | Browser application lifetime only; no post-tab continuation guarantee. |
| Durable source files | Not stored separately for resumption; only current document storage after accepted transfer. |
| Transport | One multipart request per item on the optional path. |
| Retry allowance | Four total attempts: initial plus three additional transient retries. |
| Idempotency identity | Stable selected occurrence within its server session. |
| Duplicate race handling | Transactional same-session fingerprint claim plus existing-job lookup when duplicates are disabled. |
| Settings timing | Valid settings are copied into the session at creation. |
| Status freshness | Immediate local events plus active polling at no more than 2 seconds. |
| Interruption detection | Heartbeat lease and later stale reconciliation. |
| Optional allowlist | PDF, MD, DOCX, TXT, JPG, PNG, aligned on both clients and servers. |
| Individual size units | Raw bytes; defaults use binary MiB values (4 × 1024 × 1024 and 100 × 1024 × 1024). |
| Downstream behavior | Existing `Queued` publication/scoring path without scoring changes. |

**Unresolved clarifications**: None.
