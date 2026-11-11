# Implementation Plan: Optional Parallel File Uploads

**Branch**: `001-parallel-file-upload` | **Date**: 2026-09-22 | **Spec**: `C:\code\awr-cv-match-client\specs\001-parallel-file-upload\spec.md`  
**Input**: Feature specification from `C:\code\awr-cv-match-client\specs\001-parallel-file-upload\spec.md`

## Summary

Add an explicitly opt-in, browser-lifetime upload coordinator that first records a durable
upload session and one durable item per selected occurrence, then transfers each eligible
file through a new individual-file endpoint with session-snapshotted concurrency and
raw-byte limits. The coordinator lives above route/dialog lifetime, so it continues while
the originating tab remains open. Stack B presents detailed status only on Job Details,
with a newest-action Uploads pipeline stage and a dashboard Active Uploads count; Stack A
retains its shell status surface. Server storage makes each item-to-application completion
idempotent, preserves the established duplicate rules, and publishes every successful
application into the existing queued scoring flow.

The existing bulk upload UI/API/handler path remains the default and is not refactored or
routed through the new coordinator. Optional-path settings are system-administrator-only
and independent from all scoring controls. Both Stack A and Stack B implement the same
HTTP, persistence, authorization, status, and validation contract against the shared
SQLite/Azure SQL model. Implementation is strictly sequenced: Stack B defines and proves
the contract and additive schema first, its legacy and optional-path regression gate must
pass, and only then may Stack A implementation begin.

## Technical Context

**Language/Version**: TypeScript 5.7 on Node.js 20.19+/22.12+ with React 19; C# on .NET 10 with Blazor WebAssembly and ASP.NET Core 10  
**Primary Dependencies**: Vite 7, Express 4, React Context/hooks, existing `src/lib/api.ts` client boundary, better-sqlite3 12, `mssql` 11; MediatR 12, FluentValidation 12, EF Core 10 (SQLite and SQL Server), typed `HttpClient`/Blazor scoped services  
**Storage**: Shared `shared-data/talentmatch.db` SQLite database locally and Azure SQL under the `talentmatch` schema in cloud deployments; existing document bytes remain in `DocumentBlobs`/current storage path  
**Testing**: Vitest 4 + Testing Library/jsdom for Stack A, Playwright 1.58 for browser journeys, xUnit 2.9 + bUnit 2.7 + `Microsoft.AspNetCore.Mvc.Testing` for Stack B  
**Target Platform**: Modern desktop/mobile browsers with one originating tab; Linux-hosted Node.js/ASP.NET Core servers; SQLite development and Azure SQL production providers  
**Project Type**: Dual-stack web application sharing behavioral, API, authorization, and database contracts  
**Performance Goals**: In a 100-file optional session, never exceed the snapshotted file-count or active raw-byte limits; capacity-only waiting never fails a file; at least 95% of status changes appear within 2 seconds; dialog close/navigation is available within 2 seconds; one file failure does not affect the other 99  
**Constraints**: Optional mode defaults off; legacy bulk execution is byte-for-byte behaviorally isolated; Stack B must be implemented and pass its stack-specific regression gate before any Stack A source or migration implementation begins; default optional limits are 4 concurrent files, 4 MiB per file, and 100 MiB in flight; a transient item receives at most 3 additional attempts; local file transfer is not durable beyond tab lifetime; no scoring behavior/capacity changes; client and server validate the same optional-path type and size rules  
**Scale/Scope**: Sessions of at least 100 selected occurrences, one job per session, one application at most per occurrence, both technology stacks, two database providers, administrator settings, aggregate/item status, retries, audit/telemetry, and targeted legacy regression coverage

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

### Pre-design gate

| Principle | Gate evaluation |
| --- | --- |
| I. Typed & Auditable | **PASS** — the plan requires shared typed session/item/settings/contracts, stable correlation IDs, immutable audit events for settings, session, attempt, and state changes, and actor/timestamp metadata. |
| II. Layered Architecture | **PASS** — Stack A UI calls only `src/lib/api.ts`; routes delegate optional-path state changes to services/repositories. Stack B uses Domain/Application/Infrastructure/Web boundaries with MediatR and EF Core. |
| III. Storage Abstraction | **PASS** — all new persistence is exposed through Stack A repository/provider interfaces and Stack B domain repository interfaces implemented by Infrastructure; routes/components never import concrete providers. |
| IV. Security Defaults | **PASS** — current job authorization remains in force, upload settings require the existing global `admin` role on the server, and optional uploads receive client and server type/size validation. No SAS or client secrets are introduced. |
| V. LLM Integration | **PASS / not changed** — successful applications enter the existing queued/scoring path; no LLM call, prompt, model, or scoring rule changes. |
| VI. UI Precision | **PASS** — the aggregate surface has explicit waiting/loading/error/terminal states, actionable per-file outcomes, navigation-safe updates, and accessible structured labels. |
| VII. Simplicity & YAGNI | **PASS** — no service worker, desktop agent, cloud-folder ingest, durable local-file cache, upload runner pool, or scoring changes are planned. Browser-lifetime coordination and durable metadata are directly required. |
| VIII. System Communication | **PASS** — document transfer and scoring handoff remain within existing client/API/storage/scoring boundaries. |
| IX. Clean Architecture (.NET) | **PASS** — entities and repository contracts are Domain-owned, orchestration/validation is in Application, EF/persistence in Infrastructure, and endpoints/Blazor UI in Web. |

**Gate result**: PASS. No constitutional violation requires justification.

## Phase 0 Research Decisions

The decisions and alternatives are recorded in
`C:\code\awr-cv-match-client\specs\001-parallel-file-upload\research.md`.
All technical unknowns are resolved; no `NEEDS CLARIFICATION` remains.

## Phase 1 Design

### Compatibility boundary

The current path remains:

- Stack A: `UploadApplicationsDialog.handleUpload` →
  `api.uploadApplications` → `POST /api/jobs/:jobId/applications/upload` →
  current application/document persistence and `Queued` result.
- Stack B: `UploadApplications.UploadFiles` →
  `ApiClient.UploadApplicationsAsync` → the existing multipart upload endpoint →
  `UploadApplicationsCommandHandler` → `PublishUploadedAsync` and the existing
  scoring-queue pulse.

When optional mode is off, no new session, item, scheduler, settings lookup, request
shape, validation branch, retry behavior, status surface dependency, or endpoint is used.
Existing close blocking, progress, warnings, duplicate behavior, response shapes, and
downstream publication remain unchanged. The opt-in control resets to off whenever a new
selection flow begins.

### Stack definitions and mandatory delivery sequence

Repository context defines the stacks and their upload compatibility boundaries as:

- **Stack B — .NET/Blazor Clean Architecture (implemented first)**:
  `dotnet/src/Web.Client/Components/UploadApplications.razor` →
  `dotnet/src/Web.Client/Services/ApiClient.cs` →
  `dotnet/src/Web.Server/Endpoints/ApplicationsEndpoints.cs` →
  `dotnet/src/Application/Applications/Commands/UploadApplicationsCommand.cs` →
  Domain repository contracts and EF Core Infrastructure persistence. The legacy command
  persists applications as `Uploading`, calls `PublishUploadedAsync`, changes the returned
  status to `Queued`, and pulses the existing scoring queue.
- **Stack A — React/TypeScript and Express (implemented second)**:
  `src/components/UploadApplicationsDialog.tsx` →
  `src/lib/api-real.ts` →
  `server/routes/applications.ts` →
  the existing storage repositories and audit service. Its legacy route accepts one
  base64 JSON batch and persists successful applications directly as `Queued`.

The different legacy request, transaction, publication, progress, and failure semantics
are intentional compatibility boundaries; neither stack may be refactored toward the
other as part of this feature.

Implementation and later `tasks.md` generation MUST use these dependency phases:

1. **B0 — Stack B baseline and contract freeze**: run and record the existing Stack B
   upload tests; confirm the OpenAPI and data-model artifacts map to Domain/Application/
   Infrastructure/Web boundaries. Planning artifacts may remain cross-stack, but no
   Stack A source or migration is changed.
2. **B1 — Stack B server and persistence**: add C# entities/contracts, EF Core mappings
   and additive migration, repositories, settings/session/item application services,
   authorized endpoints, idempotent individual completion, retry classification,
   audit/telemetry, and the unchanged `Queued` publication handoff.
3. **B2 — Stack B client**: add typed `ApiClient` methods, scoped WASM coordinator,
   deterministic count/byte scheduler, Job Details pipeline/status drill-down, dashboard
   active count, dialog opt-in branch, and global-administrator Settings UI.
4. **B-GATE — Stack B regression gate**: validate the unchanged optional-off multipart
   path and all new Stack B behavior. The gate requires targeted upload tests, provider
   migration tests, the full .NET test suite, and a Release build to pass without changed
   legacy expectations. Stack A work is blocked until this gate is recorded as passed.
5. **A0 — Stack A contract adoption**: after B-GATE only, map the proven HTTP/status/
   schema semantics into canonical TypeScript types and the existing API abstraction.
   Stack A adopts the Stack B-proven schema; it does not redesign it.
6. **A1 — Stack A server and persistence**: add matching idempotent SQLite/Azure SQL
   evolution, repositories, services, authorized Express adapters, audit/telemetry, and
   individual completion while preserving the current JSON bulk route exactly.
7. **A2 — Stack A client**: add typed API methods, React application-lifetime
   coordinator, deterministic scheduler, shell status, item details, dialog opt-in
   branch, and global-administrator Settings UI.
8. **A-GATE — Stack A regression gate**: validate the unchanged optional-off JSON path,
   the new Stack A path, migration compatibility with Stack B's schema, and all Node/
   browser build, lint, unit, integration, and end-to-end checks.
9. **PARITY-GATE — final cross-stack gate**: rerun contract/schema parity and the focused
   legacy-off suites in both stacks. Release requires B-GATE and A-GATE; passing Stack A
   never waives or replaces the earlier Stack B result.

For later task generation, create an explicit B-GATE verification task that depends on
every B1/B2 task. Every A0/A1/A2 task MUST depend on B-GATE, A-GATE MUST depend on all
Stack A implementation tasks, and PARITY-GATE MUST depend on both B-GATE and A-GATE.
Do not mark Stack A tasks as parallelizable with Stack B implementation or its gate.

### Optional-path flow

1. The dialog validates the current supported extension/type rules and the session's
   4 MiB-default individual limit, computes a stable client occurrence key for each
   selected occurrence, and rejects only individually invalid/oversized files.
2. If at least one item remains eligible, the client creates one server session with all
   occurrence metadata before any file content request. The server snapshots the current
   validated settings and records all items as `waiting`.
3. A browser-global coordinator, mounted for the React application lifetime or registered
   as a scoped Blazor WASM service, owns the in-memory `File`/`IBrowserFile` handles. It
   schedules one request per item only when both file-count and raw-byte permits are
   available. Eligible items that cannot start are `throttled`, not failed.
4. Each content request is addressed by session ID, item ID, and stable occurrence key.
   The server recomputes SHA-256, validates job ownership/type/size, and atomically returns
   the already-recorded terminal result or creates no more than one application for that
   item. Duplicate-disabled completion preserves current job and same-selection
   fingerprint rules; duplicate-enabled completion still enforces one application per
   occurrence.
5. Only transient transport/server-availability outcomes are retried, using bounded
   backoff for at most three additional attempts. Validation, authorization, duplicate,
   and other permanent outcomes are not retried.
6. Successful item completion calls the existing application creation/publication
   boundary and enters the same `Queued`/scoring path. Scoring concurrency is never read
   or changed by the upload coordinator.
7. Status presentation remains independent of the dialog. In Stack B, Job Details renders
   a collapsed Upload activity section plus an **Uploads** pipeline stage for the newest
   job session. The stage shows the submitted count, light-blue waiting/throttled/uploading
   work, orange failed/retrying work, and grey terminal remainder. Activating it is a
   button action that preserves `/jobs/{jobId}`, expands the related session, and focuses
   the Upload activity summary so the browser scrolls it into view. The dashboard renders
   only **Active Uploads** (`sum(Total - Terminal)`) between Applications and Queued;
   System Configuration renders no upload details. A tab-lifetime heartbeat reconciles
   browser activity; on later retrieval, stale nonterminal items become or are reported
   as `interrupted`. No source bytes are retained for automatic continuation.

### Administrator settings

- Add **Settings** under **System Configuration** for the existing global system
  administrator (`role === "admin"` / `IsInRole("admin")`) only.
- Give System Configuration submenu links a full-width wrapping hover/focus/active target.
- Use the same `h3` title hierarchy as the other System Configuration pages and align the
  three Settings labels and numeric inputs in a two-column grid that becomes one column
  on narrow screens.
- Persist one current upload-settings record containing positive whole-number file
  concurrency, maximum individual bytes, and total in-flight bytes. Total bytes must be
  greater than or equal to maximum individual bytes.
- Return defaults (4, 4 MiB, 100 MiB) when no row exists. There is no separate
  upload-runner/worker setting because the current repository has no independent upload
  runner pool.
- A save validates atomically, retains the last valid record on failure, uses optimistic
  versioning, and emits an immutable audit event. Sessions copy values at creation and
  never re-read live settings.

### Persistence and migration strategy

- Add shared `UploadSettings`, `UploadSessions`, and `UploadItems` tables/entities with
  provider-neutral repository contracts, foreign keys to Jobs/Applications, status and
  value constraints, and unique `(SessionId, OccurrenceKey)` idempotency.
- Add and validate the EF Core code-first migration/model snapshot in Stack B first.
  Only after B-GATE passes, add matching idempotent SQLite/Azure SQL schema evolution to
  Stack A. Both stacks target the Stack B-proven table and column semantics.
- Do not alter or backfill existing Applications, ApplicationDocuments, document blobs,
  upload requests, or scoring rows. No legacy row is converted to an upload session.
- An absent settings row is valid and resolves to defaults, making deployment
  backward-compatible before the first administrator save.
- Deploy additive schema and read-compatible server code before exposing the opt-in UI.
  Stack B migration and rollback compatibility are proven before Stack A adopts the
  schema. Rolling back either UI leaves its legacy endpoint operational; optional records
  remain inert and inspectable.

### Validation and parser alignment

- Preserve the legacy validators exactly on the legacy path, including its current
  warnings and limits.
- Centralize only the new optional path's allowlist/MIME normalization so its dialog,
  individual endpoint, and both stack implementations agree. The allowlist is the
  existing application-dialog set: PDF, Markdown, DOCX, TXT, JPG, and PNG.
- Enforce the snapshotted optional maximum against raw bytes, not base64 or multipart
  envelope size. A file exactly at the limit is accepted; one byte over is rejected as
  an item-specific permanent validation outcome.
- Keep parser/scoring submission unchanged. The optional endpoint persists the same
  document metadata/content format that existing scoring workers consume. Express JSON
  body-size behavior is not reused for the new content endpoint; the per-item transport
  must accept at least the configured raw maximum plus encoding overhead, or use
  multipart/binary transport.

### Targeted regression coverage

- **Stack B legacy-off gate** extends
  `dotnet/tests/Web.Tests/JobUsabilityComponentsTests.cs`,
  `dotnet/tests/Application.Tests/UploadApplicationsCommandTests.cs`, and
  `dotnet/tests/Infrastructure.Tests/UploadedApplicationPublicationTests.cs` to prove
  the existing multipart endpoint/client method is called once with the same batch and
  duplicate flag, the dialog retains its current progress/close/result behavior, no
  session/settings API is contacted, all-or-none legacy publication is unchanged, and
  successful applications still become `Queued` and pulse the scoring queue.
- **Stack B optional-path gate** adds Domain/Application/Infrastructure/Web tests for
  create-before-content, settings validation/snapshotting, global-admin authorization,
  transaction/unique-key races, duplicate parity, legal state transitions, terminal
  replay, stale-to-interrupted reconciliation, audit correlation, SQLite/SQL Server
  migration behavior, deterministic count/byte scheduling, bounded retries, navigation
  lifetime, item explanations, and exact/over-limit files. B-GATE runs focused tests,
  `dotnet test TalentMatch.slnx`, and
  `dotnet build TalentMatch.slnx --configuration Release`.
- **Stack A legacy-off gate**, which cannot start before B-GATE, extends
  `tests/unit/api-real-auth-transport.test.ts`,
  `tests/integration/application-upload-duplicates.test.ts`, and component coverage for
  `UploadApplicationsDialog` to prove the existing JSON endpoint is called once with the
  same base64 batch and duplicate flag, current validation/warnings/progress/close/result
  behavior is unchanged, no session/settings API is contacted, and successful
  applications remain directly `Queued`.
- **Stack A optional-path gate** adds repository/service/route, Testing Library, scheduler,
  and Playwright coverage equivalent to the proven Stack B contract, including schema
  adoption against both providers, global-admin authorization, duplicate/idempotency
  races, retries, browser-global navigation lifetime, observability, and the 100-file
  criteria. A-GATE runs focused tests, `npm run test`, `npm run build`, `npm run lint`,
  and `npm run test:e2e`.
- **Final parity gate** replays the common contract fixtures and provider schema checks,
  confirms the OpenAPI shapes and structured statuses match, and reruns both stacks'
  legacy-off upload tests. No legacy expectation may be changed merely to make either
  gate pass.

## Project Structure

### Documentation (this feature)

```text
C:\code\awr-cv-match-client\specs\001-parallel-file-upload\
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts\
│   └── openapi.yaml
└── tasks.md                 # intentionally not created by this planning workflow
```

### Source Code (repository root)

```text
C:\code\awr-cv-match-client\
├── src\
│   ├── components\         # dialog opt-in, aggregate surface, admin Settings UI
│   ├── lib\                # typed API-client additions only
│   ├── providers\          # browser-global upload coordinator/state
│   └── types\index.ts      # canonical Stack A feature contracts
├── server\
│   ├── routes\             # thin session/item/settings HTTP adapters
│   ├── services\           # scheduling-independent upload orchestration/audit rules
│   └── storage\
│       ├── repos\          # provider-neutral persistence operations
│       └── schema-sqlite.sql
├── tests\
│   ├── unit\
│   ├── integration\
│   └── e2e\
├── dotnet\
│   ├── src\
│   │   ├── Domain\         # entities and repository interfaces
│   │   ├── Application\    # commands, queries, validators, state transitions
│   │   ├── Infrastructure\ # EF mappings, migration, repository implementations
│   │   ├── Web.Server\     # authorized endpoints only
│   │   └── Web.Client\     # scoped coordinator, job status/dashboard count, dialog, Settings page
│   └── tests\
│       ├── Domain.Tests\
│       ├── Application.Tests\
│       ├── Infrastructure.Tests\
│       └── Web.Tests\
└── INTEGRATION.md          # equivalent endpoint mapping and contracts
```

**Structure Decision**: Extend the two existing application stacks in place. The shared
contract and database semantics are the compatibility seam; no third project, external
queue, worker service, or new storage technology is introduced. The browser coordinator
controls only client-side transfer scheduling, while all durable truth and idempotency
remain server-side.

## Post-design Constitution Check

| Principle | Post-design result |
| --- | --- |
| Typed/auditable state | **PASS** — every entity, DTO, transition, actor, timestamp, correlation ID, and attempt is represented in the model and contract. |
| Layering/storage abstraction | **PASS** — the contract places UI, HTTP, orchestration, and persistence responsibilities in existing boundaries for both stacks. |
| Security/validation | **PASS** — authorization is server-enforced and the optional raw-byte/type limits align at client and server without weakening legacy behavior. |
| LLM/scoring discipline | **PASS** — the handoff ends at the existing `Queued` publication boundary. |
| UI precision | **PASS** — persistent aggregate and item states, errors, retries, and interruption are explicit. |
| Simplicity/YAGNI | **PASS** — one browser coordinator and three durable entities satisfy the stated requirements without post-tab transfer machinery or a runner pool. |
| Cross-stack architecture | **PASS** — identical external contracts and schema semantics are implemented through each stack's mandated architecture. |

**Post-design gate result**: PASS. No violations, gate failures, or unresolved
clarifications.

## Complexity Tracking

No constitution violations require justification.
