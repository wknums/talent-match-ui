# Tasks: 008 Platform-Mode Orchestration Shift

**Input**: Design documents from `/specs/008-platform-mode-shift/`
**Prerequisites**: `plan.md` (required), `spec.md` (required), `research.md`, `data-model.md`, `contracts/platform-batch-api.md`, `quickstart.md`

**Tests**: Included because spec defines measurable criteria MC-008-001..MC-008-005.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Keep feature artifacts and contract source aligned before implementation.

- [X] T001 Create feature spec artifact in `specs/008-platform-mode-shift/spec.md`.
- [X] T002 Create implementation plan artifact in `specs/008-platform-mode-shift/plan.md`.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Establish shared constraints and contract behavior required by all user stories.

**CRITICAL**: User story implementation starts only after this phase.

- [X] T003 Align storage-provider wording and canonical-source rules in `specs/008-platform-mode-shift/platform-contract.md`.
- [ ] T032 Add platform-mode startup precondition validation (`STORAGE_PROVIDER=azuresql`, blob account present) in `server/index.ts` and Stack B startup composition root under `dotnet/src/Web.Server/`.

**Checkpoint**: Contract and startup prerequisites are enforced for both stacks.

---

## Phase 3: User Story 1 - Recruiter runs job in platform mode (Priority: P1)

**Goal**: Submit and reconcile platform batches with correct terminal semantics, idempotency behavior, and retry split behavior.

**Independent Test**: A mixed-outcome batch completes only when all per-CV outcomes are terminal/persisted, failed CVs can be retried via a new batch, and idempotent replay does not create duplicates.

### Tests for User Story 1

- [ ] T053 [P] [US1] Validate MC-008-001 mixed terminal semantics in `tests/integration/platform-mode-terminal-semantics.test.ts` and `dotnet/tests/Integration.Tests/PlatformModeTerminalSemanticsTests.cs`.
- [ ] T054 [P] [US1] Validate MC-008-002 retry split correctness in `tests/integration/platform-mode-retry-split.test.ts` and `dotnet/tests/Integration.Tests/PlatformModeRetrySplitTests.cs`.
- [ ] T055 [P] [US1] Validate MC-008-003 idempotency retention window in `tests/integration/platform-mode-idempotency-window.test.ts` and `dotnet/tests/Integration.Tests/PlatformModeIdempotencyWindowTests.cs`.

### Implementation for User Story 1

- [ ] T030 [US1] Align Stack B cancellation reconciliation behavior with contract partial-result semantics in `dotnet/src/Infrastructure/HostedServices/PlatformScoringReconciler.cs`.
- [ ] T031 [P] [US1] Align per-CV aggregated payload handling in Stack B with Stack A in `dotnet/src/Infrastructure/Services/PlatformScoringService.cs`.
- [ ] T033 [US1] Implement FR-008-002 and FR-008-002A idempotent submit enforcement (`Idempotency-Key == batchId` and replay handling) in `server/services/platform-submitter.ts`, `dotnet/src/Infrastructure/Services/PlatformScoringService.cs`, and `specs/008-platform-mode-shift/platform-contract.md`.
- [ ] T034 [US1] Implement FR-008-014 mixed terminal batch semantics in `server/workers/reconciler.ts`, `server/services/platform-submitter.ts`, and `dotnet/src/Infrastructure/HostedServices/PlatformScoringReconciler.cs`.
- [ ] T035 [US1] Implement FR-008-015 failed-CV retry split with source-batch immutability in `server/storage/repos/scoring-batch-repo.ts`, `server/routes/jobs.ts`, `dotnet/src/Application/Jobs/Commands/`, `dotnet/src/Infrastructure/Persistence/Repositories/ScoringBatchRepository.cs`, and `dotnet/src/Web.Server/Endpoints/JobsEndpoints.cs`.

**Checkpoint**: Platform-mode submit/poll/cancel/finalization behavior is independently testable.

---

## Phase 4: User Story 2 - Operator switches modes (Priority: P2)

**Goal**: Ensure cross-mode byte compatibility and no-duplicate-byte storage behavior for both stacks.

**Independent Test**: Records created in one mode remain processable in the other mode for both BlobUri-present and DB-inline cases.

### Tests for User Story 2

- [ ] T052 [US2] Execute mode-switch scenario in `specs/008-platform-mode-shift/quickstart.md` using integration coverage in `tests/integration/` and `dotnet/tests/Integration.Tests/`.

### Implementation for User Story 2

- [X] T010 [P] [US2] Persist `BlobUri` and `ContentSha256` after platform uploads in `server/services/platform-submitter.ts`.
- [X] T011 [US2] Add repository support for blob metadata persistence and no-duplicate-byte behavior in `server/storage/repos/application-repo.ts`.
- [X] T012 [US2] Implement BlobUri-first and DB-fallback document reads in `server/storage/repos/application-repo.ts`.
- [X] T013 [P] [US2] Extend shared document typing for blob metadata in `src/types/index.ts`.
- [X] T020 [P] [US2] Add blob metadata fields to `dotnet/src/Domain/Entities/ApplicationDocument.cs`.
- [X] T021 [US2] Map blob metadata columns in `dotnet/src/Infrastructure/Persistence/AppDbContext.cs`.
- [X] T022 [P] [US2] Extend blob URI read support in `dotnet/src/Application/Common/Interfaces/IBlobStore.cs` and `dotnet/src/Infrastructure/Services/BlobStores.cs`.
- [X] T023 [US2] Add repository contract and implementation for blob metadata persistence and duplicate-byte removal in `dotnet/src/Domain/Interfaces/IApplicationRepository.cs` and `dotnet/src/Infrastructure/Persistence/Repositories/ApplicationRepository.cs`.
- [X] T024 [US2] Implement BlobUri-first and DB-fallback byte resolution in `dotnet/src/Infrastructure/Services/PlatformScoringService.cs` and `dotnet/src/Application/Scoring/Commands/ScoreApplicationCommand.cs`.
- [X] T025 [US2] Update constructor-based tests for score command handlers in `dotnet/tests/Application.Tests/ScoreApplicationCommandEvidenceParsingTests.cs` and `dotnet/tests/Application.Tests/ManualReviewPrepopulationTests.cs`.

**Checkpoint**: Cross-mode compatibility and canonical-byte rules are independently testable.

---

## Phase 5: User Story 3 - Security and governance (Priority: P3)

**Goal**: Enforce observability and audit-retention requirements while keeping governance constraints intact.

**Independent Test**: Submit/poll/cancel/fallback flows emit required structured telemetry and decision traces are queryable through configured retention.

### Tests for User Story 3

- [ ] T056 [P] [US3] Validate MC-008-004 structured log and metric coverage in `tests/integration/platform-mode-observability.test.ts` and `dotnet/tests/Integration.Tests/PlatformModeObservabilityTests.cs`.
- [ ] T057 [P] [US3] Validate MC-008-005 decision-trace auditability in `tests/integration/platform-mode-decision-trace-retention.test.ts` and `dotnet/tests/Integration.Tests/PlatformModeDecisionTraceRetentionTests.cs`.

### Implementation for User Story 3

- [ ] T036 [US3] Implement NFR-008-004 observability instrumentation for submit/poll/cancel/fallback paths in `server/services/platform-submitter.ts`, `server/workers/reconciler.ts`, `dotnet/src/Infrastructure/Services/PlatformScoringService.cs`, and `dotnet/src/Infrastructure/HostedServices/PlatformScoringReconciler.cs`.
- [ ] T037 [US3] Implement NFR-008-005 per-CV decision-trace retention model in `server/storage/schema.sql`, `server/storage/schema-sqlite.sql`, `server/storage/repos/`, `dotnet/src/Domain/Entities/`, `dotnet/src/Infrastructure/Persistence/`, and `dotnet/src/Infrastructure/Persistence/Repositories/`.
- [ ] T038 [P] [US3] Add retention-window configuration and cleanup policy in `.env.example`, `dotnet/src/Web.Server/appsettings*.json`, and `infra/terraform/`.

**Checkpoint**: Security/governance requirements are independently testable.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Final synchronization, regression checks, and release readiness.

- [ ] T040 [P] Update implementation status table in `specs/008-platform-mode-shift/README.md`.
- [ ] T041 [P] Synchronize stale platform-contract references in `specs/001-talent-matching-platform/`.
- [ ] T042 [P] Update environment and quickstart docs for `AWR_PLATFORM_MAX_INFLIGHT_PER_TICK` and storage semantics in `specs/008-platform-mode-shift/quickstart.md` and repo docs.
- [X] T050 [P] Run Stack A checks for changed files via project scripts in `package.json`.
- [X] T051 [P] Run Stack B build/tests for changed files via solution and test projects under `dotnet/`.

---

## Phase 7: User Story 4 - Recruiter/Admin searches by applicant name (Priority: P1)

**Goal**: Recruiter/admin users can locate job applications by applicant name in a case-insensitive way and open the existing application detail page in both stacks.

**Independent Test**: Searching by mixed-case applicant name finds matching application(s) in each stack and View navigation opens the current detail page route.

### Implementation for User Story 4

- [X] T058 [US4] Implement case-insensitive applicant-name filtering support for Stack A applications API + UI in `server/routes/applications.ts`, `src/lib/api-real.ts`, `src/lib/api.ts`, and `src/components/JobDetailView.tsx`.
- [X] T059 [US4] Implement Stack B applicant-name search in job applications UI and API contract, including candidate-field persistence, in `dotnet/src/Domain/Entities/Application.cs`, `dotnet/src/Application/Applications/Commands/UploadApplicationsCommand.cs`, `dotnet/src/Application/Applications/Queries/GetApplicationsQuery.cs`, `dotnet/src/Web.Server/Endpoints/ApplicationsEndpoints.cs`, `dotnet/src/Web.Server/Program.cs`, `dotnet/src/Infrastructure/Persistence/AppDbContext.cs`, `dotnet/src/Web.Client/Services/ApiClient.cs`, and `dotnet/src/Web.Client/Pages/JobDetail.razor`.

---

## Dependencies & Execution Order

### Phase Dependencies

- Setup (Phase 1) has no dependencies.
- Foundational (Phase 2) depends on Setup and blocks all user stories.
- User Story phases (Phases 3-5) depend on Foundational completion.
- Polish (Phase 6) depends on desired user story phases being complete.

### User Story Dependencies

- US1 (P1) starts immediately after Foundational.
- US2 (P2) starts after Foundational; it may run in parallel with US1 where files do not overlap.
- US3 (P3) starts after Foundational; observability tasks can run in parallel with late US1/US2 tasks where file scopes differ.

### Requirement Coverage (New FR/MC)

- FR-008-002 and FR-008-002A: T033 (implementation), T055 (validation).
- FR-008-014: T034 (implementation), T053 (validation).
- FR-008-015: T035 (implementation), T054 (validation).
- NFR-008-004: T036 (implementation), T056 (validation).
- NFR-008-005: T037 and T038 (implementation), T057 (validation).
- MC-008-001: T053.
- MC-008-002: T054.
- MC-008-003: T055.
- MC-008-004: T056.
- MC-008-005: T057.
- FR-008-016 and MC-008-006: T058 and T059.

---

## Parallel Examples

### User Story 1

- Run in parallel: T053, T054, T055.
- Run in parallel: T031 with investigation/setup work for T030.

### User Story 2

- Run in parallel: T010, T013, T020, T022.
- Then sequence: T011/T012 and T021/T023/T024/T025.

### User Story 3

- Run in parallel: T056, T057, T038.
- Then sequence: T036 and T037.

---

## Implementation Strategy

### MVP First (US1)

1. Complete Phase 1 and Phase 2.
2. Complete US1 tasks (Phase 3).
3. Validate T053-T055 before moving forward.

### Incremental Delivery

1. Deliver US1 behavior and validation.
2. Deliver US2 cross-mode compatibility and mode-switch validation.
3. Deliver US3 observability and audit retention.
4. Complete polish and regression checks.

### Parallel Team Strategy

1. Team aligns on Phase 1 and 2 first.
2. After Phase 2, split by story lanes (US1/US2/US3) using [P] tasks.
3. Rejoin for Phase 6 polish and release verification.
