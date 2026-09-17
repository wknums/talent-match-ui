# Tasks: Configurable Rubric Generation and Editing

**Input**: Design documents from `/specs/001-dynamic-rubric-editor/`
**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, quickstart.md, contracts/

**Tests**: Explicitly included. The feature spec, quickstart runbook, and user request require contract, integration, component, accessibility, parity, and regression coverage.

**Organization**: Tasks are grouped by user story so each story can be implemented and validated independently across Stack A and Stack B.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on incomplete tasks)
- **[Story]**: User story label for traceability (`[US1]`, `[US2]`, `[US3]`, `[US4]`)
- Every task includes exact file paths

## Path Conventions

- **Stack A server**: `server/`
- **Stack A client**: `src/`
- **Stack A tests**: `tests/`
- **Stack B server/application/domain/infrastructure**: `dotnet/src/`
- **Stack B tests**: `dotnet/tests/`
- **Feature contracts and parity artifacts**: `specs/001-dynamic-rubric-editor/`

---

## Phase 1: Setup

**Purpose**: Create the shared fixture and parity artifacts that all later contract, migration, and parity work depends on.

- [X] T001 Create the shared extraction fixture corpus in `specs/001-dynamic-rubric-editor/contracts/fixtures/sample-spec.md`, `specs/001-dynamic-rubric-editor/contracts/fixtures/valid-itemized-extraction.json`, `specs/001-dynamic-rubric-editor/contracts/fixtures/compound-requirements.json`, `specs/001-dynamic-rubric-editor/contracts/fixtures/true-duplicates.json`, `specs/001-dynamic-rubric-editor/contracts/fixtures/ambiguous-category.json`, `specs/001-dynamic-rubric-editor/contracts/fixtures/invalid-missing-fields.json`, and `specs/001-dynamic-rubric-editor/contracts/fixtures/invalid-weight-total.json`
- [X] T002 [P] Create expected normalized outputs in `specs/001-dynamic-rubric-editor/contracts/fixtures/expected-valid-rubric-v2.json`, `specs/001-dynamic-rubric-editor/contracts/fixtures/expected-compound-rubric-v2.json`, `specs/001-dynamic-rubric-editor/contracts/fixtures/expected-ambiguous-rubric-v2.json`, and `specs/001-dynamic-rubric-editor/contracts/fixtures/expected-legacy-conversion.json`
- [X] T003 [P] Create the feature parity checklist scaffold in `specs/001-dynamic-rubric-editor/checklists/parity.md` covering shared fixtures, Stack A, Stack B, and the four user stories

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Establish the shared contracts, schema, repositories, and seed behavior that every user story depends on.

**⚠️ CRITICAL**: No user story work should begin until this phase is complete.

- [X] T004 [P] Extend shared TypeScript contracts for extraction findings, instruction versions, extraction records, rubric-v2 categories/items, and legacy-conversion state in `src/types/index.ts`, `src/lib/api-real.ts`, `src/lib/api.ts`, and `src/lib/api-mock.ts`
- [X] T005 [P] Add shared .NET DTO/model contracts for extraction findings, instruction versions, extraction records, and rubric-v2 envelopes in `dotnet/src/Application/ExtractionInstructions/Models/ExtractionInstructionModels.cs`, `dotnet/src/Application/JobExtraction/Models/JobSpecExtractionModels.cs`, `dotnet/src/Application/Rubrics/Models/RubricModels.cs`, and `dotnet/src/Web.Client/Services/ApiClient.cs`
- [X] T006 Create the Stack A protected-contract and legacy-adapter services in `server/services/extraction-contract.ts` and `server/services/rubric-conversion.ts` using `specs/001-dynamic-rubric-editor/contracts/extraction-rubric.schema.json` and `specs/001-dynamic-rubric-editor/contracts/rubric-config.schema.json`
- [X] T007 Create the Stack B protected-contract and legacy-adapter services in `dotnet/src/Application/JobExtraction/Services/JobSpecExtractionContractValidator.cs`, `dotnet/src/Application/Rubrics/Services/LegacyRubricAdapter.cs`, and `dotnet/src/Application/Rubrics/Services/RubricOrderNormalizer.cs` using `specs/001-dynamic-rubric-editor/contracts/extraction-rubric.schema.json` and `specs/001-dynamic-rubric-editor/contracts/rubric-config.schema.json`
- [X] T008 Add shared schema changes for `ExtractionInstructionVersions`, `JobSpecExtractions`, filtered active-version constraints, and `JobConfigVersions` extraction metadata columns in `server/storage/schema.sql`, `server/storage/schema-sqlite.sql`, and `server/storage/schema-pre-batch-upgrades.sql`
- [X] T009 [P] Implement Stack A storage contracts and repositories for instruction/extraction history in `server/storage/types.ts`, `server/storage/repos/extraction-instruction-repo.ts`, `server/storage/repos/job-spec-extraction-repo.ts`, and `server/storage/repos/index.ts`
- [X] T010 [P] Implement Stack B entities, interfaces, repositories, and EF registrations for instruction/extraction history in `dotnet/src/Domain/Entities/ExtractionInstructionVersion.cs`, `dotnet/src/Domain/Entities/JobSpecExtraction.cs`, `dotnet/src/Domain/Interfaces/IExtractionInstructionRepository.cs`, `dotnet/src/Domain/Interfaces/IJobSpecExtractionRepository.cs`, `dotnet/src/Infrastructure/Persistence/Repositories/ExtractionInstructionRepository.cs`, `dotnet/src/Infrastructure/Persistence/Repositories/JobSpecExtractionRepository.cs`, `dotnet/src/Infrastructure/Persistence/AppDbContext.cs`, and `dotnet/src/Infrastructure/DependencyInjection.cs`
- [X] T011 Add idempotent Stack A instruction seeding and startup reconciliation in `server/storage/db.ts` and `server/index.ts` so version 1 is created only when `ExtractionInstructionVersions` is empty
- [X] T012 Add idempotent Stack B instruction seeding and EF migration snapshot updates in `dotnet/src/Web.Server/Program.cs`, `dotnet/src/Infrastructure/Persistence/Migrations/20260909_AddExtractionInstructionLifecycle.cs`, and `dotnet/src/Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs`
- [X] T013 Add shared fixture-loader and schema verification helpers in `tests/setup.ts`, `tests/integration/extraction-instructions.test.ts`, and `dotnet/tests/Application.Tests/TestFixtureLoader.cs`

**Checkpoint**: Shared contracts, storage, and seed behavior are in place; user-story work can now proceed with parity-safe foundations.

---

## Phase 3: User Story 1 - Generate an Itemized Rubric (Priority: P1) 🎯 MVP

**Goal**: Every independently assessable job requirement extracted from a specification becomes its own traceable rubric item in a suitable category or is explicitly flagged for review.

**Independent Test**: Upload a representative specification and verify that each requirement appears once as its own editable rubric item, compound statements are split, true duplicates are consolidated, and ambiguous items are retained as `needs_review`.

### Tests for User Story 1 ⚠️

> **NOTE**: Write these tests first, verify they fail, then implement the story.

- [X] T014 [P] [US1] Add Stack A extraction completeness and contract tests in `tests/unit/extraction-contract.test.ts` and `tests/integration/job-spec-extraction.test.ts` using the fixtures in `specs/001-dynamic-rubric-editor/contracts/fixtures/`
- [X] T015 [P] [US1] Add Stack B extraction completeness and contract tests in `dotnet/tests/Application.Tests/JobSpecExtractionContractTests.cs`, `dotnet/tests/Infrastructure.Tests/JobSpecExtractionRepositoryTests.cs`, and `dotnet/tests/Web.Tests/JobSpecExtractionEndpointsTests.cs`

### Implementation for User Story 1

- [X] T016 [US1] Implement the Stack A extraction orchestrator in `server/services/job-spec-extraction.ts` and `server/services/extraction-contract.ts` to compose the active instruction, append the protected contract, normalize individual requirements, persist `JobSpecExtraction`, and return actionable findings
- [X] T017 [US1] Replace hardcoded Stack A extraction flows in `server/routes/jobs.ts` and `server/storage/repos/job-repo.ts` so `/api/jobs/extract-spec`, `/api/jobs/extract-rubric`, job creation, and config updates read/write `rubric-v2`, `ExtractionId`, and `ExtractionInstructionVersionId`
- [X] T018 [US1] Implement the Stack B extraction orchestrator in `dotnet/src/Application/JobExtraction/Commands/ExtractJobSpecCommand.cs`, `dotnet/src/Application/JobExtraction/Services/JobSpecExtractionOrchestrator.cs`, and `dotnet/src/Infrastructure/Services/AwrJobSpecExtractionService.cs` to compose the active instruction, append the protected contract, normalize individual requirements, persist `JobSpecExtraction`, and return actionable findings
- [X] T019 [US1] Replace hardcoded Stack B extraction flows in `dotnet/src/Web.Server/Endpoints/JobsEndpoints.cs`, `dotnet/src/Application/Jobs/Commands/CreateJobCommand.cs`, `dotnet/src/Application/Jobs/Commands/UpdateJobConfigCommand.cs`, and `dotnet/src/Application/Jobs/Queries/GetJobConfigQuery.cs` so extracted job configs read/write `rubric-v2`, `ExtractionId`, and `ExtractionInstructionVersionId`
- [X] T020 [P] [US1] Update Stack A recruiter create/edit APIs and components for itemized extraction payloads in `src/lib/api-real.ts`, `src/lib/api.ts`, `src/components/CreateJobDialog.tsx`, `src/components/RubricEditor.tsx`, and `src/components/RubricItemCard.tsx`
- [X] T021 [P] [US1] Update Stack B recruiter create/edit APIs and components for itemized extraction payloads in `dotnet/src/Web.Client/Services/ApiClient.cs`, `dotnet/src/Web.Client/Components/CreateJobDialog.razor`, `dotnet/src/Web.Client/Components/RubricEditor.razor`, and `dotnet/src/Web.Client/Components/RubricItemCard.razor`
- [X] T022 [US1] Add shared upload-flow parity coverage for itemized extraction in `tests/e2e/stack-b-job-onboarding.spec.ts`, `tests/integration/job-spec-extraction.test.ts`, and `specs/001-dynamic-rubric-editor/checklists/parity.md`

**Checkpoint**: User Story 1 is complete when both stacks generate the same itemized rubric shape from the shared fixture corpus and persist traceable extraction metadata.

---

## Phase 4: User Story 2 - Reorganize Rubric Requirements (Priority: P2)

**Goal**: Recruiters can move, reorder, add, edit, and remove individual rubric items with accessible pointer, keyboard, and touch controls while preserving traceability and save safety.

**Independent Test**: Open a generated rubric, move items across categories and within a category, use keyboard/touch equivalents, save, reopen, and verify that order, category assignment, and source trace persist; legacy rubrics must require controlled conversion before item-level editing.

### Tests for User Story 2 ⚠️

- [X] T023 [P] [US2] Add Stack A rubric editor interaction tests in `tests/unit/rubric-editor.test.tsx`, `tests/integration/rubric-schema-migration.test.ts`, and `tests/unit/rubric-conversion.test.ts`
- [X] T024 [P] [US2] Add Stack B rubric editor and legacy-conversion tests in `dotnet/tests/Web.Tests/RubricEditorTests.cs`, `dotnet/tests/Application.Tests/UpdateJobConfigRubricV2Tests.cs`, and `dotnet/tests/Infrastructure.Tests/JobConfigVersionMappingTests.cs`

### Implementation for User Story 2

- [X] T025 [US2] Implement Stack A `rubric-v2` persistence, stale-write detection, and explicit legacy conversion in `server/services/rubric-conversion.ts`, `server/storage/repos/job-repo.ts`, `server/routes/jobs.ts`, `src/lib/api-real.ts`, and `src/lib/api.ts`
- [X] T026 [US2] Implement Stack B `rubric-v2` persistence, stale-write detection, and explicit legacy conversion in `dotnet/src/Application/Rubrics/Services/LegacyRubricAdapter.cs`, `dotnet/src/Application/Jobs/Commands/UpdateJobConfigCommand.cs`, `dotnet/src/Application/Jobs/Queries/GetJobDetailQuery.cs`, `dotnet/src/Web.Server/Endpoints/JobsEndpoints.cs`, and `dotnet/src/Web.Client/Services/ApiClient.cs`
- [X] T027 [P] [US2] Build Stack A accessible drag/drop, keyboard move, touch move, add/edit/remove item, and empty-category states in `src/components/RubricEditor.tsx`, `src/components/RubricItemCard.tsx`, `src/components/CreateJobDialog.tsx`, and `src/components/JobDetailView.tsx`
- [X] T028 [P] [US2] Build Stack B accessible drag/drop, keyboard move, touch move, add/edit/remove item, and empty-category states in `dotnet/src/Web.Client/Components/RubricEditor.razor`, `dotnet/src/Web.Client/Components/RubricItemCard.razor`, `dotnet/src/Web.Client/Components/CreateJobDialog.razor`, and `dotnet/src/Web.Client/Pages/JobDetail.razor`
- [X] T029 [US2] Preserve category-based scoring, prompt generation, application detail, and manual-review compatibility with `rubric-v2` in `src/components/ApplicationDetail.tsx`, `src/components/ManualReviewView.tsx`, `src/lib/stackb-scoring.ts`, `dotnet/src/Web.Client/Pages/ApplicationDetail.razor`, `dotnet/src/Web.Client/Pages/ManualReview.razor`, `dotnet/src/Application/Prompts/Commands/GeneratePromptCommand.cs`, and `dotnet/src/Application/Scoring/Commands/ScoreApplicationCommand.cs`
- [X] T030 [US2] Add touch and save-failure regression coverage for item moves and legacy conversion in `tests/e2e/stack-b-rubric-editor.spec.ts`, `tests/unit/rubric-editor.test.tsx`, and `dotnet/tests/Web.Tests/LegacyRubricConversionTests.cs`

**Checkpoint**: User Story 2 is complete when item-level rubric editing is accessible, persistent, concurrency-safe, and compatible with legacy configs and downstream scoring.

---

## Phase 5: User Story 3 - Administer the Default Generation Prompt (Priority: P3)

**Goal**: Administrators can draft, validate, activate, and roll back extraction-instruction versions without changing the protected response contract or requiring a code release.

**Independent Test**: Create a new instruction draft, validate it against a sample specification, inspect the result, activate it, verify later extractions use the new version, then roll back to a prior valid version; non-admin mutations must fail with `403`.

### Tests for User Story 3 ⚠️

- [X] T031 [P] [US3] Add Stack A extraction-instruction lifecycle tests in `tests/integration/extraction-instructions.test.ts` and `tests/unit/extraction-instruction-admin.test.tsx`
- [X] T032 [P] [US3] Add Stack B extraction-instruction lifecycle tests in `dotnet/tests/Application.Tests/ExtractionInstructionCommandTests.cs`, `dotnet/tests/Web.Tests/ExtractionInstructionEndpointsTests.cs`, and `dotnet/tests/Web.Tests/ExtractionInstructionAdminTests.cs`

### Implementation for User Story 3

- [X] T033 [US3] Implement Stack A draft/validate/activate/rollback endpoints and audit wiring in `server/routes/extraction-instructions.ts`, `server/storage/repos/extraction-instruction-repo.ts`, `server/services/job-spec-extraction.ts`, `server/services/audit.ts`, and `server/index.ts`
- [X] T034 [US3] Implement Stack B draft/validate/activate/rollback commands, queries, and endpoint registration in `dotnet/src/Application/ExtractionInstructions/Commands/CreateExtractionInstructionCommand.cs`, `dotnet/src/Application/ExtractionInstructions/Commands/ValidateExtractionInstructionCommand.cs`, `dotnet/src/Application/ExtractionInstructions/Commands/ActivateExtractionInstructionCommand.cs`, `dotnet/src/Application/ExtractionInstructions/Queries/GetExtractionInstructionsQuery.cs`, `dotnet/src/Application/ExtractionInstructions/Queries/GetExtractionInstructionQuery.cs`, `dotnet/src/Web.Server/Endpoints/ExtractionInstructionEndpoints.cs`, and `dotnet/src/Web.Server/Program.cs`
- [X] T035 [P] [US3] Build Stack A admin instruction-management UI and menu entry in `src/components/ExtractionInstructionAdmin.tsx`, `src/components/UserMenu.tsx`, `src/App.tsx`, `src/lib/api-real.ts`, and `src/lib/api.ts`
- [X] T036 [P] [US3] Build Stack B admin instruction-management UI and navigation entry in `dotnet/src/Web.Client/Components/ExtractionInstructionAdmin.razor`, `dotnet/src/Web.Client/Pages/ExtractionInstructions.razor`, `dotnet/src/Web.Client/Layout/NavMenu.razor`, and `dotnet/src/Web.Client/Services/ApiClient.cs`
- [X] T037 [US3] Enforce admin-only mutations, stale-version conflicts, and rollback audit parity in `server/routes/extraction-instructions.ts`, `server/services/authorization-errors.ts`, `dotnet/src/Web.Server/Endpoints/ExtractionInstructionEndpoints.cs`, and `dotnet/tests/Web.Tests/AuthorizationParityTests.cs`

**Checkpoint**: User Story 3 is complete when prompt lifecycle changes are admin-only, contract-safe, auditable, and immediately govern future extraction runs only.

---

## Phase 6: User Story 4 - Diagnose Extraction Results (Priority: P4)

**Goal**: Authorized reviewers can see which instruction version produced a rubric, inspect validation findings and completeness warnings, and compare extracted items back to source wording.

**Independent Test**: Generate a rubric with a known active instruction version, open the saved result, and verify that extraction time, instruction version, warnings, `needs_review` items, and source traces are visible without exposing raw prompt or secret content.

### Tests for User Story 4 ⚠️

- [X] T038 [P] [US4] Add Stack A extraction diagnostics tests in `tests/integration/extraction-diagnostics.test.ts` and `tests/unit/job-spec-extraction-record.test.ts`
- [X] T039 [P] [US4] Add Stack B extraction diagnostics tests in `dotnet/tests/Application.Tests/JobSpecExtractionDiagnosticsTests.cs` and `dotnet/tests/Web.Tests/JobSpecExtractionDiagnosticsTests.cs`

### Implementation for User Story 4

- [X] T040 [US4] Implement Stack A extraction diagnostics queries and response contracts in `server/storage/repos/job-spec-extraction-repo.ts`, `server/routes/jobs.ts`, `src/types/index.ts`, and `src/lib/api-real.ts` so job config responses expose instruction version, extraction time, completeness warnings, and source traces
- [X] T041 [US4] Implement Stack B extraction diagnostics queries and response contracts in `dotnet/src/Application/JobExtraction/Queries/GetJobSpecExtractionQuery.cs`, `dotnet/src/Web.Server/Endpoints/JobsEndpoints.cs`, `dotnet/src/Web.Client/Services/ApiClient.cs`, and `dotnet/src/Domain/Entities/JobConfigVersion.cs` so job config responses expose instruction version, extraction time, completeness warnings, and source traces
- [X] T042 [P] [US4] Surface Stack A extraction diagnostics and source-comparison UI in `src/components/CreateJobDialog.tsx`, `src/components/JobDetailView.tsx`, and `src/components/ApplicationDetail.tsx`
- [X] T043 [P] [US4] Surface Stack B extraction diagnostics and source-comparison UI in `dotnet/src/Web.Client/Components/CreateJobDialog.razor`, `dotnet/src/Web.Client/Pages/JobDetail.razor`, and `dotnet/src/Web.Client/Pages/ApplicationDetail.razor`
- [X] T044 [US4] Add shared authorized-review parity checks for `needs_review` items and version traceability in `specs/001-dynamic-rubric-editor/checklists/parity.md`, `tests/integration/extraction-diagnostics.test.ts`, and `dotnet/tests/Web.Tests/JobSpecExtractionDiagnosticsTests.cs`

**Checkpoint**: User Story 4 is complete when extraction provenance and completeness diagnostics are visible, auditable, and equivalent across both stacks.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Finish documentation, audit/security hardening, and final parity verification across all stories.

- [X] T045 [P] Update API and feature documentation for extraction-instruction lifecycle, `rubric-v2`, and legacy conversion in `INTEGRATION.md`, `README.md`, and `specs/001-dynamic-rubric-editor/quickstart.md`
- [X] T046 [P] Harden audit/security handling for prompt content, raw model output, and correlation-aware error mapping in `server/routes/extraction-instructions.ts`, `server/routes/jobs.ts`, `server/middleware/error-handler.ts`, `dotnet/src/Web.Server/Endpoints/ExtractionInstructionEndpoints.cs`, and `dotnet/src/Web.Server/Endpoints/JobsEndpoints.cs`
- [X] T047 [P] Refresh cross-stack parity reporting in `PARITY.md` and `specs/001-dynamic-rubric-editor/checklists/parity.md` after Stack A and Stack B pass the shared fixture, lifecycle, editor, and diagnostics suites
- [X] T048 Run the full acceptance runbook in `specs/001-dynamic-rubric-editor/quickstart.md` and record fixture, build, and parity results in `specs/001-dynamic-rubric-editor/checklists/parity.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies; start immediately
- **Foundational (Phase 2)**: Depends on Phase 1; blocks all user stories
- **User Story 1 (Phase 3)**: Depends on Phase 2
- **User Story 2 (Phase 4)**: Depends on Phase 3 because it requires the itemized `rubric-v2` payloads, stable item IDs, and extraction metadata from US1
- **User Story 3 (Phase 5)**: Depends on Phase 3 because validation/activation reuses the extraction orchestration and protected-contract enforcement delivered for US1
- **User Story 4 (Phase 6)**: Depends on Phase 3 and Phase 5 because diagnostics require persisted extraction records plus versioned instruction lifecycle metadata
- **Polish (Phase 7)**: Depends on all desired user stories being complete

### User Story Dependencies

- **US1 (P1)**: First shippable increment after foundations; no dependency on later stories
- **US2 (P2)**: Requires US1 extraction output and `rubric-v2` persistence before item movement and legacy conversion are meaningful
- **US3 (P3)**: Requires US1 extraction orchestration so draft validation and later extractions share the same contract-safe pipeline
- **US4 (P4)**: Requires US1 extraction records and US3 instruction-version history to present trustworthy diagnostics

### Within Each User Story

- Tests must be written and observed failing before implementation
- Contract validation and normalization before endpoint wiring
- Persistence and repositories before UI save flows
- API/client surface changes before component polish
- Story-level parity verification before moving to the next dependent story

### Parallel Opportunities

- T002 and T003 can run in parallel after T001
- T004/T005, T009/T010, and T011/T012 can run in parallel because they split by stack
- T014/T015, T020/T021, T023/T024, T027/T028, T031/T032, T035/T036, T038/T039, and T042/T043 can run in parallel by stack
- T045, T046, and T047 can run in parallel once all user stories are complete

---

## Parallel Example: User Story 1

```text
Task: T014 [US1] Stack A extraction completeness tests
Task: T015 [US1] Stack B extraction completeness tests

Task: T020 [US1] Stack A recruiter extraction UI updates
Task: T021 [US1] Stack B recruiter extraction UI updates
```

## Parallel Example: User Story 2

```text
Task: T023 [US2] Stack A rubric editor tests
Task: T024 [US2] Stack B rubric editor tests

Task: T027 [US2] Stack A accessible item-move UI
Task: T028 [US2] Stack B accessible item-move UI
```

## Parallel Example: User Story 3

```text
Task: T031 [US3] Stack A instruction lifecycle tests
Task: T032 [US3] Stack B instruction lifecycle tests

Task: T035 [US3] Stack A instruction admin UI
Task: T036 [US3] Stack B instruction admin UI
```

## Parallel Example: User Story 4

```text
Task: T038 [US4] Stack A extraction diagnostics tests
Task: T039 [US4] Stack B extraction diagnostics tests

Task: T042 [US4] Stack A diagnostics UI
Task: T043 [US4] Stack B diagnostics UI
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational
3. Complete Phase 3: User Story 1
4. Validate the shared fixture corpus and recruiter upload flow
5. Stop and demo the itemized extraction pipeline before taking on editing or admin lifecycle work

### Incremental Delivery

1. Ship **US1** to establish contract-safe itemized extraction
2. Add **US2** to enable accessible rubric editing and legacy conversion
3. Add **US3** to make extraction behavior configurable by administrators
4. Add **US4** to expose provenance and diagnostics for investigation
5. Finish with documentation, security, and parity verification

### Parallel Team Strategy

1. One pair completes shared fixtures, schema, and repository foundations
2. After Phase 2:
   - Developer A: Stack A work inside the current story
   - Developer B: Stack B work inside the current story
   - Developer C: Shared parity/tests/documentation follow-up for that story
3. Re-converge at each checkpoint before advancing dependent stories

---

## Notes

- `rubric-v2` is the required persisted shape for all new writes
- Legacy rubrics must remain readable and convert only through an explicit user-confirmed flow
- Extraction validation must fail visibly; do not coerce malformed AWReason output into success-shaped payloads
- Category-level scoring remains the downstream compatibility boundary even after itemization
- Do not log full prompt content, raw source documents, or secrets in audit/event payloads

## Follow-up: continuous sequential scoring (2026-09-16)

- [X] Q001 Replace fixed upload snapshots with the in-process document pool in `dotnet/src/Infrastructure/HostedServices/SequentialScoringPool.cs` and reuse single-document scoring in `dotnet/src/Application/Common/Services/SequentialApplicationScorer.cs`.
- [X] Q002 Add ready-upload publication and atomic claim/lease/failure/retry persistence in `dotnet/src/Infrastructure/Persistence/Repositories/ApplicationRepository.cs` and `dotnet/src/Infrastructure/Persistence/Repositories/SequentialScoringQueueRepository.cs`, with additive shared schema upgrades.
- [X] Q003 Wire upload/process/retry triggers and immediate acceptance responses in `dotnet/src/Application/Applications/Commands/`, `dotnet/src/Application/Jobs/Commands/ProcessJobCommand.cs`, and `dotnet/src/Web.Server/Endpoints/JobsEndpoints.cs`; report persisted errors in `dotnet/src/Web.Client/Pages/JobDetail.razor`.
- [X] Q004 Verify seven-plus-seven with ten slots, refill after success/failure, cross-scope claims, readiness, approval/test exclusions, ownership recovery, and request acceptance in `dotnet/tests/Application.Tests/`, `dotnet/tests/Infrastructure.Tests/`, and `dotnet/tests/Web.Tests/`.
- [X] Q005 Document capacity, deployment, recovery, and Stack B-only scheduling scope in `INTEGRATION.md`, `PARITY.md`, and deployment environment examples.
- [X] Q006 Accept approved `rubric-v2` objects alongside legacy arrays in `dotnet/src/Infrastructure/Persistence/Repositories/SequentialScoringQueueRepository.cs`; verify malformed envelopes, approval gates, paging, and 200-slot scheduler behavior in `dotnet/tests/Infrastructure.Tests/`.
- [X] Q007 Add an authorized rolling-hour metric with 24 hourly windows from preserved first aggregate timestamps in `dotnet/src/Application/Stats/`, `dotnet/src/Infrastructure/Persistence/Repositories/ScoringThroughputRepository.cs`, and `dotnet/src/Web.Server/Endpoints/StatsEndpoints.cs`.
- [X] Q008 Add the 10-second-refresh throughput card and accessible chart to the Stack B dashboard in `dotnet/src/Web.Client/Pages/Dashboard.razor` and `dotnet/src/Web.Client/Components/ScoringThroughputPanel.razor`; verify query boundaries, scope, repository history, API and UI behavior in `dotnet/tests/`.
