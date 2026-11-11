# Tasks: Optional Parallel File Uploads

**Input**: Design documents from `C:\code\awr-cv-match-client\specs\001-parallel-file-upload\`  
**Prerequisites**: `plan.md`, `spec.md`, `research.md`, `data-model.md`, `contracts/openapi.yaml`, `quickstart.md`  
**Tests**: Regression, contract, gate, and parity tests are required by the feature specification.  
**Sequencing rule**: Stack B (.NET/Blazor Clean Architecture) is implemented and validated first. No Stack A source, test, or migration task may start before T043 (B-GATE) passes.

## Format

Every task uses `- [ ] Tnnn [P?] [US?] Description with precise path and acceptance condition`.

---

## Phase 1: Setup — Stack B Baseline and Contract Freeze

**Purpose**: Record the current Stack B behavior and freeze the additive contract before implementation.

- [X] T001 Run the existing Stack B upload baselines from `C:\code\awr-cv-match-client\dotnet\tests\Application.Tests\UploadApplicationsCommandTests.cs`, `C:\code\awr-cv-match-client\dotnet\tests\Infrastructure.Tests\UploadedApplicationPublicationTests.cs`, and `C:\code\awr-cv-match-client\dotnet\tests\Web.Tests\JobUsabilityComponentsTests.cs`, and record commands/results in `C:\code\awr-cv-match-client\specs\001-parallel-file-upload\validation\stack-b-baseline.md`; acceptance: all pre-feature expectations and any pre-existing failures are captured before a source change.
- [X] T002 [P] Create shared defaults, status, MIME, boundary-size, authorization, and error fixtures from `C:\code\awr-cv-match-client\specs\001-parallel-file-upload\contracts\openapi.yaml` and `C:\code\awr-cv-match-client\specs\001-parallel-file-upload\data-model.md` in `C:\code\awr-cv-match-client\specs\001-parallel-file-upload\contracts\upload-contract-fixtures.json`; acceptance: the fixture contains 4, 4194304, 104857600, all eight item statuses, all six MIME types, and exact-limit/one-byte-over cases without changing the OpenAPI contract.
- [X] T003 Document the Stack B Domain/Application/Infrastructure/Web ownership, legacy endpoint isolation, and prohibited Stack A changes in `C:\code\awr-cv-match-client\specs\001-parallel-file-upload\validation\stack-b-contract-map.md`; acceptance: every OpenAPI operation and each of UploadSettings, UploadSession, and UploadItem maps to one owning layer and the existing multipart endpoint remains outside the new contract.

---

## Phase 2: Foundational — Shared Stack B Domain and Persistence

**Purpose**: Add the Stack B-only domain and additive persistence foundation used by all three stories.

**Critical**: This phase may change only `dotnet\` sources. Stack A remains untouched until B-GATE.

- [X] T004 [P] Define UploadSettings and its absent-row defaults and invariants in `C:\code\awr-cv-match-client\dotnet\src\Domain\Entities\UploadSettings.cs`; acceptance: the model represents optimistic versioning, positive whole-number limits, 4/4194304/104857600 defaults, and no runner/worker field.
- [X] T005 [P] Define UploadSession, its settings snapshot, aggregate counters, ownership, heartbeat, and active/completed invariant in `C:\code\awr-cv-match-client\dotnet\src\Domain\Entities\UploadSession.cs`; acceptance: completion is possible only when all durable items are terminal and later settings changes cannot mutate the snapshot.
- [X] T006 [P] Define UploadItem, UploadItemStatus, legal transitions, four-attempt ceiling, occurrence identity, and terminal outcome invariants in `C:\code\awr-cv-match-client\dotnet\src\Domain\Entities\UploadItem.cs`; acceptance: only waiting, throttled, uploading, retrying, succeeded, skipped_duplicate, failed, and interrupted are representable and ApplicationId is write-once.
- [X] T007 Add provider-neutral settings/session/item transaction operations to `C:\code\awr-cv-match-client\dotnet\src\Domain\Interfaces\IUploadSettingsRepository.cs` and `C:\code\awr-cv-match-client\dotnet\src\Domain\Interfaces\IUploadSessionRepository.cs`; acceptance: interfaces cover defaults, optimistic saves, atomic session-plus-items creation, terminal replay, fingerprint claims, aggregates, heartbeat reconciliation, and owner/job-scoped reads without provider SQL.
- [X] T008 Map UploadSettings, UploadSession, and UploadItem with all foreign keys, checks, concurrency tokens, filtered/unique keys, and indexes in `C:\code\awr-cv-match-client\dotnet\src\Infrastructure\Persistence\AppDbContext.cs`; acceptance: `(SessionId, OccurrenceKey)`, `(SessionId, Ordinal)`, and non-null ApplicationId uniqueness are enforced and no existing entity mapping changes semantically.
- [X] T009 Generate the additive EF Core migration in `C:\code\awr-cv-match-client\dotnet\src\Infrastructure\Persistence\Migrations\20260922231530_AddOptionalParallelUploads.cs`, its designer in `C:\code\awr-cv-match-client\dotnet\src\Infrastructure\Persistence\Migrations\20260922231530_AddOptionalParallelUploads.Designer.cs`, and update `C:\code\awr-cv-match-client\dotnet\src\Infrastructure\Persistence\Migrations\AppDbContextModelSnapshot.cs`; acceptance: apply/rollback creates or removes only UploadSettings, UploadSessions, and UploadItems for SQLite and SQL Server, with no legacy-row backfill or alteration.
- [X] T010 Register the new repository and application service boundaries in `C:\code\awr-cv-match-client\dotnet\src\Infrastructure\DependencyInjection.cs` and `C:\code\awr-cv-match-client\dotnet\src\Application\DependencyInjection.cs`; acceptance: Web projects resolve abstractions only and no route or component imports EF Core implementations.

**Checkpoint**: Stack B domain and additive schema are ready; Stack A is still unchanged.

---

## Phase 3: Stack B — User Story 1: Choose an Optional Background Upload (Priority: P1) 🎯 MVP

**Goal**: A recruiter can deliberately choose the optional individual-file path, close the dialog, navigate in the same tab, and still send eligible applications through the existing queued/scoring publication boundary.

**Independent Test**: In Stack B, enable the optional control for multiple valid files, verify the session and every occurrence commit before the first content request, close the dialog, navigate, and confirm scheduling stays within snapshotted count/raw-byte limits while successful items become Queued through the existing publication flow. Repeat with the option off and verify the legacy multipart path is unchanged.

### Tests for User Story 1

- [X] T011 [P] [US1] Extend legacy-off bUnit regression coverage in `C:\code\awr-cv-match-client\dotnet\tests\Web.Tests\JobUsabilityComponentsTests.cs`; acceptance: tests fail if the option does not reset off, the duplicate default/batch/progress/warnings/close/results change, `ApiClient.UploadApplicationsAsync` is not called exactly once, or any session/settings API is contacted.
- [X] T012 [P] [US1] Extend legacy command/publication regression coverage in `C:\code\awr-cv-match-client\dotnet\tests\Application.Tests\UploadApplicationsCommandTests.cs` and `C:\code\awr-cv-match-client\dotnet\tests\Infrastructure.Tests\UploadedApplicationPublicationTests.cs`; acceptance: tests preserve the existing all-or-none multipart behavior, Queued result, scoring-queue pulse, duplicate semantics, and absence of UploadSession/UploadItem writes when optional mode is off.
- [X] T013 [P] [US1] Add failing create-before-content and eligibility tests in `C:\code\awr-cv-match-client\dotnet\tests\Application.Tests\OptionalUploadSessionTests.cs`; acceptance: tests cover atomic session/all-occurrence creation, no-session for zero eligible items, six supported types including empty-MIME normalization, exact 4194304-byte acceptance, one-byte-over rejection, and immutable default limits.
- [X] T014 [P] [US1] Add failing optional endpoint/job-authorization and queued-publication tests in `C:\code\awr-cv-match-client\dotnet\tests\Web.Tests\OptionalUploadEndpointsTests.cs`; acceptance: tests cover OpenAPI request/response shapes, owner/job scope, multipart single-file enforcement, unsupported type/size outcomes, and successful handoff to the unchanged publication boundary.
- [X] T015 [P] [US1] Add deterministic scheduler tests in `C:\code\awr-cv-match-client\dotnet\tests\Web.Tests\UploadCoordinatorTests.cs`; acceptance: deferred streams prove selection-order scheduling, maximum active count, maximum 104857600 raw bytes, throttled-not-failed capacity waiting, permit release, 100-file bounds, and independence from scoring settings.

### Implementation for User Story 1

- [X] T016 [US1] Add OpenAPI-aligned session/item DTOs, optional MIME normalization, raw-size validators, and field-specific validation errors in `C:\code\awr-cv-match-client\dotnet\src\Application\Uploads\Models\UploadModels.cs` and `C:\code\awr-cv-match-client\dotnet\src\Application\Uploads\UploadValidators.cs`; acceptance: only PDF, MD, DOCX, TXT, JPG, and PNG pass, exact snapshot limit passes, one byte over fails only that item, and legacy validators are not referenced or changed.
- [X] T017 [US1] Implement atomic session and all-item creation in `C:\code\awr-cv-match-client\dotnet\src\Application\Uploads\Commands\CreateUploadSessionCommand.cs`; acceptance: no content can be accepted before commit, zero eligible items returns item-specific validation, stable occurrence keys/ordinals are preserved, defaults are snapshotted when settings are absent, and a ProcessingEvent is appended.
- [X] T018 [US1] Implement the initial idempotent individual-content completion and existing publication handoff in `C:\code\awr-cv-match-client\dotnet\src\Application\Uploads\Commands\UploadItemContentCommand.cs`; acceptance: actual bytes and SHA-256 are verified, duplicate-off job/selection checks and duplicate-on per-occurrence behavior hold, terminal replay creates no second Application, success uses `PublishUploadedAsync` and Queued/scoring pulse behavior, and permanent outcomes are not retried.
- [X] T019 [US1] Implement provider-neutral EF persistence for atomic session creation and content completion in `C:\code\awr-cv-match-client\dotnet\src\Infrastructure\Persistence\Repositories\UploadSessionRepository.cs`; acceptance: SQLite serialized writes and SQL Server transactions enforce occurrence/application uniqueness and same-session fingerprint decisions under concurrent requests.
- [X] T020 [US1] Add authenticated create-session and individual multipart-content adapters in `C:\code\awr-cv-match-client\dotnet\src\Web.Server\Endpoints\UploadEndpoints.cs` and register them in `C:\code\awr-cv-match-client\dotnet\src\Web.Server\Program.cs`; acceptance: routes exactly match `POST /api/jobs/{jobId}/upload-sessions` and `POST /api/upload-sessions/{sessionId}/items/{itemId}/content`, enforce existing job authorization, return canonical correlation-aware errors, and leave `ApplicationsEndpoints.cs` unchanged.
- [X] T021 [US1] Add typed create-session and upload-item multipart methods in `C:\code\awr-cv-match-client\dotnet\src\Web.Client\Services\ApiClient.cs`; acceptance: Idempotency-Key and X-Correlation-ID are sent, one raw file is streamed per request, canonical item outcomes are parsed, and `UploadApplicationsAsync` is unchanged.
- [X] T022 [US1] Implement the application-lifetime count/raw-byte scheduler in `C:\code\awr-cv-match-client\dotnet\src\Web.Client\Services\UploadCoordinator.cs` and register it as scoped in `C:\code\awr-cv-match-client\dotnet\src\Web.Client\Program.cs`; acceptance: both permits are reserved before transfer and released in `finally`, capacity-only waits are throttled in selection order, local file handles remain only for the tab lifetime, and navigation does not dispose active work.
- [X] T023 [US1] Mount the coordinator independently of dialog lifetime in `C:\code\awr-cv-match-client\dotnet\src\Web.Client\App.razor` and `C:\code\awr-cv-match-client\dotnet\src\Web.Client\Layout\MainLayout.razor`; acceptance: closing the dialog or navigating within the WASM app does not cancel requests and no service worker, durable local-file cache, or scoring control is introduced.
- [X] T024 [US1] Add the opt-in control and isolated optional branch in `C:\code\awr-cv-match-client\dotnet\src\Web.Client\Components\UploadApplications.razor`; acceptance: the control is next to Allow duplicate documents, resets off for every new selection flow, optional mode creates the session before enqueuing content, and off mode executes the original `UploadFiles` behavior byte-for-byte with its existing close/progress/results semantics.

**Checkpoint**: Stack B US1 is independently testable; Stack A remains blocked.

---

## Phase 4: Stack B — User Story 2: Monitor and Isolate File Outcomes (Priority: P2)

**Goal**: Recruiters can observe durable aggregate/item status, retries, duplicates, failures, and interruption without one item stopping another.

**Independent Test**: Run a mixed Stack B session with success, duplicate, transient-then-success, exhausted transient failure, permanent failure, and tab loss; verify eight canonical statuses, no more than four attempts, isolated outcomes, terminal replay, correlated audit/log data, sub-two-second active updates, and durable interrupted reconciliation.

### Tests for User Story 2

- [X] T025 [P] [US2] Add failing state-machine and aggregate invariant tests in `C:\code\awr-cv-match-client\dotnet\tests\Domain.Tests\UploadStateTests.cs`; acceptance: every allowed transition passes, every illegal/terminal transition fails, counters sum to TotalItemCount, and completed requires every item terminal.
- [X] T026 [P] [US2] Add failing retry, idempotency, duplicate, lost-response, and isolation tests in `C:\code\awr-cv-match-client\dotnet\tests\Application.Tests\UploadItemLifecycleTests.cs`; acceptance: only network timeout plus 408/429/5xx availability outcomes retry, four total attempts is the ceiling, permanent 4xx does not retry, each occurrence creates at most one Application, and unrelated items continue.
- [X] T027 [P] [US2] Add failing provider race, aggregate, heartbeat-staleness, and migration tests in `C:\code\awr-cv-match-client\dotnet\tests\Infrastructure.Tests\OptionalUploadPersistenceTests.cs`; acceptance: concurrent same-occurrence and matching-fingerprint requests are deterministic on SQLite and SQL Server, apply/rollback is additive, terminal replay is stable, and stale nonterminal items become interrupted.
- [X] T028 [P] [US2] Add failing list/detail/status/heartbeat authorization and observability tests in `C:\code\awr-cv-match-client\dotnet\tests\Web.Tests\OptionalUploadEndpointsTests.cs`; acceptance: owner/job scope, legal client-owned statuses, 409 stale versions, correlation headers, safe structured fields, and absence of file bytes/tokens in events/logs are asserted.
- [X] T029 [P] [US2] Add failing bUnit navigation, aggregate, drill-down, and actionable-outcome tests in `C:\code\awr-cv-match-client\dotnet\tests\Web.Tests\UploadStatusSurfaceTests.cs`, `C:\code\awr-cv-match-client\dotnet\tests\Web.Tests\DashboardTests.cs`, and `C:\code\awr-cv-match-client\dotnet\tests\Web.Tests\MainLayoutTests.cs`; acceptance: the newest job session drives the submitted count and colored Uploads segments, drill-down preserves the job route and focuses the expanded related session, dashboard Active Uploads equals nonterminal items and appears between Applications and Queued, global layout pages contain no Upload activity details, and filename/size/status/attempt/reason remain accessible.

### Implementation for User Story 2

- [X] T030 [US2] Implement legal transitions, transient classification, four-attempt exhaustion, heartbeat reconciliation, aggregate derivation, and terminal completion in `C:\code\awr-cv-match-client\dotnet\src\Application\Uploads\Services\UploadItemLifecycleService.cs`; acceptance: waiting/throttled never fail from capacity, retrying records NextRetryAt, one terminal item cannot roll back peers, and stale nonterminal work becomes interrupted.
- [X] T031 [US2] Extend transaction-safe item updates, aggregate recalculation, owner reads, and stale reconciliation in `C:\code\awr-cv-match-client\dotnet\src\Infrastructure\Persistence\Repositories\UploadSessionRepository.cs`; acceptance: optimistic versions reject stale writes, every durable transition updates aggregates atomically, terminal replay is read-only, and provider-specific locking stays inside Infrastructure.
- [X] T032 [US2] Add owned-session list/detail, heartbeat, and client-status endpoints plus correlation-aware structured logging in `C:\code\awr-cv-match-client\dotnet\src\Web.Server\Endpoints\UploadEndpoints.cs`; acceptance: routes and 401/403/404/409/422 behavior match OpenAPI, retrieval reconciles stale work, and logs contain IDs/attempt/status/raw bytes/duration/reason but no content or credentials.
- [X] T033 [US2] Extend `C:\code\awr-cv-match-client\dotnet\src\Web.Client\Services\UploadCoordinator.cs` with pre-transfer browser-byte staging, bounded backoff, status persistence, heartbeat-before-reconciliation, active polling at no more than two seconds, lost-response replay, and unload interruption best effort; acceptance: a 67-file selection remains available after dialog disposal, heartbeat updates are not rejected by concurrent item-version changes, short network/scale delays do not mark live files interrupted, permits release after every outcome, only transient classes retry, attempt count never exceeds four, and one failure does not stop the queue.
- [X] T034 [US2] Implement aggregate and drill-down UI in `C:\code\awr-cv-match-client\dotnet\src\Web.Client\Components\UploadStatusSurface.razor`, `C:\code\awr-cv-match-client\dotnet\src\Web.Client\Components\PipelineVisualizer.razor`, `C:\code\awr-cv-match-client\dotnet\src\Web.Client\Pages\JobDetail.razor`, and `C:\code\awr-cv-match-client\dotnet\src\Web.Client\Pages\Dashboard.razor`; acceptance: Upload activity is collapsed and exists only on Job Details, the latest job session drives a decreasing light-blue plus orange Uploads stage, button drill-down expands/focuses the related session without route navigation, dashboard shows only the nonterminal Active Uploads aggregate between Applications and Queued, Job Details obtains aggregate counts without loading every application, initially loads only shortlist, lazily loads longlist/excluded/review, and terminal sessions remain inspectable after returning to the job.
- [X] T035 [US2] Emit immutable settings/session/item attempt/state/terminal events through `C:\code\awr-cv-match-client\dotnet\src\Domain\Interfaces\IProcessingEventRepository.cs` from `C:\code\awr-cv-match-client\dotnet\src\Application\Uploads\Services\UploadItemLifecycleService.cs`; acceptance: one correlation ID joins responses, events, and logs, upload metrics are separate from scoring metrics, and safe details never contain source bytes, base64, tokens, or secrets.

**Checkpoint**: Stack B US2 is independently testable with mixed outcomes.

---

## Phase 5: Stack B — User Story 3: Configure Safe Upload Capacity (Priority: P3)

**Goal**: Global system administrators can safely view and change optional-upload limits while unauthorized users cannot and active sessions retain their snapshots.

**Independent Test**: As global admin, read absent-row defaults, save valid limits, reject each invalid/stale update atomically, and prove an active session keeps old values while the next session gets new values; as recruiter or organization admin, verify navigation is hidden and direct requests return forbidden.

### Tests for User Story 3

- [X] T036 [P] [US3] Add failing settings default, validation, atomic-save, optimistic-version, and snapshot tests in `C:\code\awr-cv-match-client\dotnet\tests\Application.Tests\UploadSettingsTests.cs`; acceptance: defaults are 4/4194304/104857600 without persistence, positive whole values and total-at-least-individual are enforced, invalid saves retain the last valid row, and active session snapshots never change.
- [X] T037 [P] [US3] Add failing global-admin endpoint authorization and audit tests in `C:\code\awr-cv-match-client\dotnet\tests\Web.Tests\UploadSettingsEndpointsTests.cs`; acceptance: admin GET/PUT succeeds, recruiter and organization admin receive canonical 403, stale versions return 409, invalid fields return 422, and valid updates emit correlated immutable audit events.
- [X] T038 [P] [US3] Add failing Settings navigation/form tests in `C:\code\awr-cv-match-client\dotnet\tests\Web.Tests\MainLayoutTests.cs` and `C:\code\awr-cv-match-client\dotnet\tests\Web.Tests\UploadSettingsPageTests.cs`; acceptance: only global admin sees System Configuration > Settings, defaults render in binary units, the title uses the shared `h3` hierarchy, three aligned field rows render, field errors are actionable, and no runner/worker, scoring setting, or Upload activity details appear.

### Implementation for User Story 3

- [X] T039 [US3] Implement absent-row defaults, validation, optimistic save, and immutable audit orchestration in `C:\code\awr-cv-match-client\dotnet\src\Application\Uploads\Commands\UpdateUploadSettingsCommand.cs` and `C:\code\awr-cv-match-client\dotnet\src\Application\Uploads\Queries\GetUploadSettingsQuery.cs`; acceptance: invalid updates are atomic, a missing row reports persisted=false/version=0, and successful saves increment version.
- [X] T040 [US3] Implement UploadSettings persistence in `C:\code\awr-cv-match-client\dotnet\src\Infrastructure\Persistence\Repositories\UploadSettingsRepository.cs` and global-admin GET/PUT adapters in `C:\code\awr-cv-match-client\dotnet\src\Web.Server\Endpoints\UploadSettingsEndpoints.cs`; acceptance: only `IsInRole("admin")` passes server authorization and canonical 200/401/403/409/422 responses match OpenAPI.
- [X] T041 [US3] Add typed settings methods in `C:\code\awr-cv-match-client\dotnet\src\Web.Client\Services\ApiClient.cs`, create `C:\code\awr-cv-match-client\dotnet\src\Web.Client\Pages\UploadSettings.razor` and its scoped CSS, and add the global-admin-only Settings link plus full-width wrapping submenu states in `C:\code\awr-cv-match-client\dotnet\src\Web.Client\Layout\NavMenu.razor` and `NavMenu.razor.css`; acceptance: valid values persist/reload, labels and inputs align responsively, submenu shading covers complete wrapped labels, field errors retain the last valid display, unauthorized users see no control, and upload settings remain separate from scoring.
- [X] T042 [US3] Integrate settings resolution into session creation in `C:\code\awr-cv-match-client\dotnet\src\Application\Uploads\Commands\CreateUploadSessionCommand.cs`; acceptance: each new session atomically copies the latest valid limits, an already-created session never re-reads them, and absence of a row copies 4/4194304/104857600.

**Checkpoint**: All Stack B stories are implemented; Stack A is still blocked.

---

## Phase 6: B-GATE — Blocking Stack B Validation

**Purpose**: Prove legacy behavior and the complete optional Stack B path before any Stack A task.

- [X] T043 Execute and record B-GATE in `C:\code\awr-cv-match-client\specs\001-parallel-file-upload\validation\b-gate.md`; acceptance: T001–T042 are complete, focused legacy-off tests prove the existing multipart call/batch/duplicate/progress/warnings/close/results/all-or-none publication/Queued/scoring pulse behavior and zero session/settings contact, all optional settings/authorization/durable-state/idempotency/duplicate/scheduler/retry/navigation/observability/parser/provider-migration tests pass, `dotnet test C:\code\awr-cv-match-client\dotnet\TalentMatch.slnx` passes, and `dotnet build C:\code\awr-cv-match-client\dotnet\TalentMatch.slnx --configuration Release` passes; any failure blocks T044–T073.

**B-GATE checkpoint**: A recorded pass is mandatory. Stack A cannot run concurrently with any incomplete Stack B task.

---

## Phase 7: Stack A — User Story 1: Choose an Optional Background Upload (Priority: P1)

**Goal**: Adopt the Stack B-proven contract in React/Express without redesign, preserving the default base64 JSON bulk path.

**Independent Test**: After B-GATE, repeat the US1 journey in Stack A and verify create-before-content, application-lifetime scheduling, configured count/raw-byte limits, navigation continuity, existing direct-Queued behavior, and an unchanged optional-off JSON batch path.

### Tests for User Story 1

- [X] T044 [P] [US1] Extend legacy API transport regression tests in `C:\code\awr-cv-match-client\tests\unit\api-real-auth-transport.test.ts`; acceptance: with optional mode off the same base64 JSON batch and duplicate flag are sent exactly once to the existing endpoint and no session/settings endpoint is called.
- [X] T045 [P] [US1] Extend legacy duplicate and direct-Queued regression tests in `C:\code\awr-cv-match-client\tests\integration\application-upload-duplicates.test.ts`; acceptance: duplicate-off/on behavior, validation, successful direct Queued persistence, and existing response shape remain unchanged with zero UploadSession/UploadItem writes.
- [X] T046 [P] [US1] Add failing legacy dialog isolation tests in `C:\code\awr-cv-match-client\tests\unit\upload-applications-dialog.test.tsx`; acceptance: option/reset defaults, warnings, progress, close blocking, result interpretation, duplicate default, and existing bulk call behavior fail on any optional-off regression.
- [X] T047 [P] [US1] Add failing Stack B-contract adoption tests in `C:\code\awr-cv-match-client\tests\integration\optional-upload-session.test.ts`; acceptance: create-before-content, owner/job authorization, six types, empty-MIME normalization, exact/over-limit outcomes, one-file multipart, idempotent completion, duplicate policy, and unchanged Queued/scoring handoff are covered.
- [X] T048 [P] [US1] Add failing deterministic React coordinator tests in `C:\code\awr-cv-match-client\tests\unit\upload-coordinator.test.tsx`; acceptance: deferred requests prove count/byte maxima including 100 files, selection-order throttling, permit release, dialog close/navigation lifetime, and no scoring-setting dependency.

### Implementation for User Story 1

- [X] T049 [US1] Add canonical TypeScript settings/session/item/status DTOs matching the proven OpenAPI and fixtures in `C:\code\awr-cv-match-client\src\types\index.ts`; acceptance: wire names, nullability, defaults, eight statuses, six MIME values, optimistic versions, and correlation/error shapes match Stack B without additive Stack A-only fields.
- [X] T050 [US1] Add idempotent equivalent UploadSettings, UploadSessions, and UploadItems DDL and constraints to `C:\code\awr-cv-match-client\server\storage\schema-sqlite.sql`, `C:\code\awr-cv-match-client\server\storage\schema.sql`, and `C:\code\awr-cv-match-client\server\storage\db.ts`; acceptance: clean and upgraded SQLite/Azure SQL databases match the Stack B-proven schema, reruns are safe, defaults require no row, and no Application/document/blob/scoring row is altered or backfilled.
- [X] T051 [US1] Add provider-neutral upload operations to `C:\code\awr-cv-match-client\server\storage\types.ts`, implement them in `C:\code\awr-cv-match-client\server\storage\repos\upload-repo.ts`, and export them from `C:\code\awr-cv-match-client\server\storage\repos\index.ts`; acceptance: atomic session/all-item creation, occurrence uniqueness, terminal replay, fingerprint claims, and SQLite/Azure SQL transaction semantics match Stack B.
- [X] T052 [US1] Implement create-session, optional validation, individual completion, duplicate decisions, SHA-256 verification, and existing direct-Queued handoff in `C:\code\awr-cv-match-client\server\services\optional-upload.ts`; acceptance: no bytes arrive before durable intent, exact raw limit passes, over-limit/type failures stay item-specific, each occurrence creates at most one Application, and the existing scoring submission contract is unchanged.
- [X] T053 [US1] Add authenticated OpenAPI-aligned create-session and multipart-content adapters in `C:\code\awr-cv-match-client\server\routes\uploads.ts` and register them in `C:\code\awr-cv-match-client\server\index.ts`; acceptance: existing job authorization and canonical errors apply, multipart accepts configured raw bytes plus envelope overhead, and `C:\code\awr-cv-match-client\server\routes\applications.ts` retains its JSON bulk behavior unchanged.
- [X] T054 [US1] Add typed create-session and upload-item multipart calls to `C:\code\awr-cv-match-client\src\lib\api-real.ts`, expose them through `C:\code\awr-cv-match-client\src\lib\api.ts`, and keep `C:\code\awr-cv-match-client\src\lib\api-mock.ts` contract-compatible; acceptance: idempotency/correlation headers and raw FormData are used only for optional mode while `uploadApplications` is unchanged.
- [X] T055 [US1] Implement application-lifetime deterministic scheduling in `C:\code\awr-cv-match-client\src\providers\UploadCoordinatorProvider.tsx`; acceptance: it snapshots File handles only for the tab, reserves/releases count and raw-byte permits around each request, marks capacity waits throttled, preserves selection order, continues across routes, and introduces no service worker or local-file persistence.
- [X] T056 [US1] Mount the provider in `C:\code\awr-cv-match-client\src\main.tsx` and add the isolated opt-in branch in `C:\code\awr-cv-match-client\src\components\UploadApplicationsDialog.tsx`; acceptance: optional mode records all items before enqueueing content and permits dialog close, while every new flow defaults off and off mode preserves the original validation/base64 batch/progress/duplicate/close/result behavior.

**Checkpoint**: Stack A US1 matches the proven Stack B contract and preserves its distinct legacy path.

---

## Phase 8: Stack A — User Story 2: Monitor and Isolate File Outcomes (Priority: P2)

**Goal**: Provide Stack A durable status, retry/isolation, observability, and navigation-safe UI equivalent to Stack B.

**Independent Test**: Repeat the mixed-outcome, retry, race, lost-response, navigation, reload, and stale-heartbeat scenarios and verify Stack A exposes the same statuses, counts, explanations, idempotency, and safe correlation data.

### Tests for User Story 2

- [X] T057 [P] [US2] Add failing repository/service lifecycle, aggregate, duplicate-race, lost-response, and retry tests in `C:\code\awr-cv-match-client\tests\integration\optional-upload-lifecycle.test.ts`; acceptance: both providers enforce one Application per occurrence, duplicate-off selection races are deterministic, only transient classes retry up to four total attempts, permanent failures do not retry, and peer items continue.
- [X] T058 [P] [US2] Add failing aggregate/drill-down React tests in `C:\code\awr-cv-match-client\tests\unit\upload-status-surface.test.tsx`; acceptance: required counts/progress and filename/raw size/status/attempt/actionable reason render accessibly, active updates arrive within two seconds, and terminal/interrupted results survive view changes.
- [X] T059 [P] [US2] Add browser-lifetime navigation, dialog reopen, reload/interruption, and partial-failure coverage in `C:\code\awr-cv-match-client\tests\e2e\optional-parallel-upload.spec.ts`; acceptance: uploads continue after dialog close and repeated in-app navigation, reopening does not duplicate a session, reload never auto-resumes local files, and server-known results remain visible.
- [X] T060 [P] [US2] Add failing correlation/audit/log safety tests in `C:\code\awr-cv-match-client\tests\integration\optional-upload-observability.test.ts`; acceptance: settings/session/attempt/state/terminal events share correlation IDs with responses/logs, expose safe dimensions, remain separate from scoring telemetry, and contain no bytes/base64/tokens/secrets.

### Implementation for User Story 2

- [X] T061 [US2] Implement legal item transitions, transient classification, four-attempt exhaustion, aggregates, completion, and heartbeat-staleness reconciliation in `C:\code\awr-cv-match-client\server\services\optional-upload.ts`; acceptance: state behavior matches Stack B, capacity never causes failure, terminal states are immutable, and stale nonterminal items become interrupted.
- [X] T062 [US2] Extend `C:\code\awr-cv-match-client\server\storage\repos\upload-repo.ts` with optimistic status updates, owner-scoped reads, provider-safe locks/transactions, aggregates, and stale reconciliation; acceptance: concurrent replays return one canonical outcome, every durable transition and aggregate commits atomically, and provider SQL does not escape the repository.
- [X] T063 [US2] Add owned list/detail, heartbeat, and client-status routes with OpenAPI status/error behavior in `C:\code\awr-cv-match-client\server\routes\uploads.ts`; acceptance: only waiting/throttled/retrying/interrupted client transitions are accepted, stale versions return 409, retrieval reconciles interruption, and authorization cannot leak another owner's session.
- [X] T064 [US2] Extend `C:\code\awr-cv-match-client\src\providers\UploadCoordinatorProvider.tsx` with bounded backoff, durable status calls, heartbeat, polling no slower than two seconds while active, lost-response replay, and unload best effort; acceptance: only timeout/408/429/5xx availability failures retry, permits always release, and a failed item does not stop unrelated work.
- [X] T065 [US2] Create `C:\code\awr-cv-match-client\src\components\UploadStatusSurface.tsx` and render it from `C:\code\awr-cv-match-client\src\App.tsx`; acceptance: all aggregate categories and per-item fields/explanations remain visible across in-app routes, terminal sessions can be reopened, and no dialog presence is required.
- [X] T066 [US2] Add correlated immutable ProcessingEvents and safe structured upload logging in `C:\code\awr-cv-match-client\server\services\optional-upload.ts` through `C:\code\awr-cv-match-client\server\services\audit.ts`; acceptance: session/item/job/actor/attempt/status/raw bytes/duration/reason are traceable, source content and credentials are excluded, and scoring metrics/settings are neither read nor changed.

**Checkpoint**: Stack A US2 is independently testable and behaviorally equivalent to Stack B.

---

## Phase 9: Stack A — User Story 3: Configure Safe Upload Capacity (Priority: P3)

**Goal**: Provide the proven global-admin Settings contract in Express/React with identical defaults, validation, authorization, versioning, and per-session snapshots.

**Independent Test**: Repeat the Stack B settings matrix in Stack A against SQLite and Azure SQL, including absent-row defaults, valid save/reload, invalid/stale atomic rejection, hidden unauthorized UI, direct 403, and old/new session snapshot behavior.

### Tests for User Story 3

- [X] T067 [P] [US3] Add failing settings repository/service/endpoint tests in `C:\code\awr-cv-match-client\tests\integration\optional-upload-settings.test.ts`; acceptance: both providers return 4/4194304/104857600 when absent, validate atomically, enforce optimistic versions and global-admin-only access, audit saves, and preserve old/new session snapshots.
- [X] T068 [P] [US3] Add failing System Configuration Settings UI tests in `C:\code\awr-cv-match-client\tests\unit\optional-upload-settings-ui.test.tsx`; acceptance: only `role === "admin"` sees Settings, defaults and field errors render, successful values reload, and no runner/worker or scoring capacity control appears.
- [X] T069 [P] [US3] Add failing SQLite/Azure SQL schema-default and cross-stack column/constraint tests in `C:\code\awr-cv-match-client\tests\integration\optional-upload-schema-parity.test.ts`; acceptance: Stack A DDL is additive/idempotent and matches Stack B table, column, key, index, concurrency, and absent-settings semantics exactly.

### Implementation for User Story 3

- [X] T070 [US3] Implement settings default reads, validated optimistic saves, and immutable audit writes in `C:\code\awr-cv-match-client\server\storage\repos\upload-repo.ts` and `C:\code\awr-cv-match-client\server\services\optional-upload.ts`; acceptance: invalid saves do not mutate the last valid row, missing-row reads do not persist, successful versions increment, and no runner/worker field exists.
- [X] T071 [US3] Add global-admin GET/PUT `/api/admin/upload-settings` adapters in `C:\code\awr-cv-match-client\server\routes\upload-settings.ts` and register them in `C:\code\awr-cv-match-client\server\index.ts`; acceptance: `authorizationContext.globalRole === "admin"` is independently enforced server-side and 200/401/403/409/422 correlation-aware shapes match OpenAPI.
- [X] T072 [US3] Add typed settings methods in `C:\code\awr-cv-match-client\src\lib\api-real.ts`, create `C:\code\awr-cv-match-client\src\components\OptionalUploadSettings.tsx`, and add the admin-only Settings entry in `C:\code\awr-cv-match-client\src\App.tsx`; acceptance: valid limits persist/reload, field errors preserve the last valid values, unauthorized users see no controls, and upload capacity remains independent of scoring.
- [X] T073 [US3] Integrate persisted settings into Stack A session creation in `C:\code\awr-cv-match-client\server\services\optional-upload.ts`; acceptance: the newest valid limits are copied once into each new session, active sessions never re-read them, and absence copies 4/4194304/104857600.

**Checkpoint**: All Stack A implementation tasks are complete and were blocked by T043.

---

## Phase 10: A-GATE — Blocking Stack A Validation

- [ ] T074 Execute and record A-GATE in `C:\code\awr-cv-match-client\specs\001-parallel-file-upload\validation\a-gate.md`; acceptance: T044–T073 are complete, focused legacy-off tests prove unchanged JSON batch/duplicate/validation/warnings/progress/close/results/direct-Queued behavior and zero session/settings contact, optional contract/schema/provider/authorization/idempotency/scheduler/retry/navigation/observability/100-file tests pass, and `npm run test`, `npm run build`, `npm run lint`, and `npm run test:e2e` all pass from `C:\code\awr-cv-match-client`; a Stack A pass does not waive T043.

---

## Phase 11: Polish and Cross-Cutting Validation

**Purpose**: Document the additive contract and prepare the final cross-stack release gate.

- [ ] T075 [P] Document all optional settings/session/item endpoints, role requirements, defaults, statuses, idempotency, retries, raw-byte limits, and legacy isolation in `C:\code\awr-cv-match-client\INTEGRATION.md`; acceptance: Stack A and Stack B mappings are equivalent, the legacy endpoints are explicitly unchanged, and no out-of-scope runner, service worker, source-file persistence, or scoring change is documented.
- [ ] T076 Execute the manual scenarios from `C:\code\awr-cv-match-client\specs\001-parallel-file-upload\quickstart.md` and record sanitized evidence in `C:\code\awr-cv-match-client\specs\001-parallel-file-upload\validation\acceptance-results.md`; acceptance: default-off regression, exact/over size, six types, zero eligible, 100-file count/byte bounds, duplicate on/off, lost response, four-attempt retry, partial failure, dialog close/navigation, reload interruption, admin authorization, snapshots, observability, and unchanged scoring are each pass/fail recorded.
- [ ] T077 [P] Add common contract/status/schema fixture replay tests in `C:\code\awr-cv-match-client\dotnet\tests\Web.Tests\OptionalUploadContractParityTests.cs` and `C:\code\awr-cv-match-client\tests\integration\optional-upload-contract-parity.test.ts`; acceptance: both stacks consume `C:\code\awr-cv-match-client\specs\001-parallel-file-upload\contracts\upload-contract-fixtures.json` and assert identical defaults, statuses, MIME values, response shapes, validation boundaries, and database semantics.

---

## Phase 12: PARITY-GATE — Final Cross-Stack Release Gate

- [ ] T078 Execute and record PARITY-GATE in `C:\code\awr-cv-match-client\specs\001-parallel-file-upload\validation\parity-gate.md`; acceptance: T043 and T074 remain passed, T075–T077 are complete, common OpenAPI/status/schema fixtures pass in both stacks and both providers, Stack B focused legacy multipart suites and Stack A focused legacy JSON suites pass again without changed expectations, optional-off creates no sessions/settings reads, and the final report confirms equivalent authorization, durable state, idempotency, limits, retries, observability, UI status, and Queued/scoring handoff.

---

## Dependencies and Execution Order

### Phase and Gate Dependencies

1. **Setup (T001–T003)** starts immediately; T001 and T002 may run in parallel, and T003 depends on both.
2. **Stack B foundation (T004–T010)** depends on T003. T004–T006 may run in parallel; T007 depends on T004–T006; T008 depends on T004–T007; T009 depends on T008; T010 depends on T007–T009.
3. **Stack B US1 tests (T011–T015)** are written before US1 implementation and may run in parallel after the foundation contracts are known. T016–T024 then proceed in model → application → persistence → endpoints/client → coordinator/UI order.
4. **Stack B US2 tests (T025–T029)** are written before T030–T035 and depend on the US1 session/content seams. US2 implementation depends on T024.
5. **Stack B US3 tests (T036–T038)** are written before T039–T042 and depend on the shared UploadSettings entity/repository contract. US3 implementation depends on T024; T042 also depends on T039–T041.
6. **B-GATE T043** depends on every task T001–T042. It is a hard blocker.
7. **Every Stack A task T044–T073 has a direct task-level dependency on B-GATE T043.** None may start, even test scaffolding or schema work, while any Stack B task or T043 is incomplete.
8. Within Stack A, US1 implementation T049–T056 follows failing tests T044–T048; US2 implementation T061–T066 follows failing tests T057–T060 and completed T056; US3 implementation T070–T073 follows failing tests T067–T069 and completed T056.
9. **A-GATE T074** depends on every Stack A task T044–T073 and on the recorded B-GATE T043 result.
10. T075–T077 depend on T074; T075 and T077 may run in parallel, while T076 depends on the implemented feature and gate results.
11. **PARITY-GATE T078** depends on T043, T074, and T075–T077. It is the final release task.

### Task-Level Implementation Chains

- Stack B persistence: `T004–T007 → T008 → T009 → T010 → T019/T031/T040`.
- Stack B optional transfer: `T016 → T017 → T018 → T019 → T020/T021 → T022 → T023/T024`.
- Stack B status: `T030 → T031 → T032/T033 → T034/T035`.
- Stack B settings: `T039 → T040/T041 → T042 → T043`.
- Stack A schema/service: `T043 → T049/T050 → T051 → T052 → T053/T054 → T055 → T056`.
- Stack A status: `T043/T056 → T061 → T062/T063 → T064 → T065/T066`.
- Stack A settings: `T043/T049/T050 → T070 → T071/T072 → T073 → T074`.
- Final gates: `T043 → T074 → T075–T077 → T078`.

### User Story Dependency Graph

```text
Stack B foundation
  ├─> B-US1 (P1 core transfer)
  │     ├─> B-US2 (P2 durable monitoring/retry)
  │     └─> B-US3 (P3 settings/snapshots)
  └──────────────────────────────> B-GATE
                                      |
                                      v
                                   A-US1
                                    /   \
                                   v     v
                                A-US2   A-US3
                                   \     /
                                    v   v
                                   A-GATE
                                      |
                                      v
                                  PARITY-GATE
```

US2 and US3 are independently testable through their own APIs/UI once their stack's US1 session seam exists. The mandatory cross-stack sequence overrides the template's usual suggestion to implement stacks or stories concurrently.

---

## Parallel Opportunities

Parallel work is allowed only where `[P]` is present:

- T001 and T002 may run together; T004–T006 are independent Stack B domain files.
- Stack B failing tests T011–T015, T025–T029, and T036–T038 may run concurrently within their respective story phase before implementation.
- After T043 passes, Stack A failing tests T044–T048, T057–T060, and T067–T069 may run concurrently within their respective story phase.
- T075 and T077 may run together after A-GATE.
- No Stack A task is parallelizable with Stack B work because every T044–T073 task directly depends on T043.

## Parallel Example: Stack B User Story 1

```text
Run together after the Stack B foundation:
T011 — legacy dialog regression tests
T012 — legacy command/publication regression tests
T013 — optional session application tests
T014 — optional endpoint tests
T015 — deterministic coordinator tests
```

## Parallel Example: Stack B User Story 2

```text
Run together after Stack B US1:
T025 — domain state tests
T026 — application lifecycle tests
T027 — provider race/migration tests
T028 — endpoint/observability tests
T029 — status-surface tests
```

## Parallel Example: Stack B User Story 3

```text
Run together after the shared settings contract exists:
T036 — settings application tests
T037 — settings endpoint authorization tests
T038 — settings UI/navigation tests
```

## Parallel Example: Stack A User Story 1

```text
Run together only after B-GATE T043:
T044 — API legacy transport tests
T045 — legacy duplicate/Queued tests
T046 — legacy dialog isolation tests
T047 — optional route/service contract tests
T048 — React coordinator tests
```

## Parallel Example: Stack A User Story 2

```text
Run together only after B-GATE T043 and Stack A US1:
T057 — lifecycle/race/retry tests
T058 — status-surface tests
T059 — browser navigation/interruption tests
T060 — observability safety tests
```

## Parallel Example: Stack A User Story 3

```text
Run together only after B-GATE T043 and the Stack A schema/types:
T067 — settings service/authorization tests
T068 — settings UI tests
T069 — provider/schema parity tests
```

---

## Implementation Strategy

### MVP First

1. Complete T001–T010.
2. Complete Stack B US1 T011–T024 and independently validate the P1 journey.
3. Complete Stack B US2 and US3 because B-GATE requires the full Stack B contract.
4. Pass B-GATE T043.
5. Complete Stack A US1 T044–T056 to deliver cross-stack P1 parity.
6. Stop and independently validate US1 in both stacks before adding Stack A US2/US3.

The suggested MVP scope is **User Story 1 in both stacks**, but Stack A cannot be included until the complete Stack B implementation has passed B-GATE.

### Incremental Delivery

1. Stack B foundation → B-US1 → B-US2 → B-US3 → B-GATE.
2. Stack A US1 → independently test the cross-stack recruiter upload journey.
3. Stack A US2 → independently test durable monitoring, retries, and isolation.
4. Stack A US3 → independently test administrator settings and snapshots.
5. A-GATE → documentation/manual acceptance/parity fixtures → PARITY-GATE.

### Regression Discipline

- Write each listed test task first and observe it fail for the intended missing behavior.
- Never change legacy expectations merely to make a gate pass.
- Optional-off must not create/read optional entities, change request shapes, or consult optional settings.
- Stack B and Stack A retain their intentionally different legacy multipart versus base64 JSON and publication semantics.

---

## Validation

- Task IDs are sequential from T001 through T078 with no gaps or duplicates.
- Story labels appear only in story phases and every story-phase task has `[US1]`, `[US2]`, or `[US3]`.
- `[P]` appears only on different-file, dependency-independent work within the same allowed phase.
- Every implementation task names precise repository paths and an executable acceptance condition.
- B-GATE T043 covers legacy-off, optional-path, provider migration, full .NET tests, and Release build.
- Every Stack A task T044–T073 explicitly depends on T043 in the dependency section.
- A-GATE T074 follows all Stack A work; PARITY-GATE T078 depends on both earlier gates.
- No task introduces a runner pool, service worker, post-tab transfer guarantee, durable local-source cache, new file type, scoring change, or other topic outside the specification.
