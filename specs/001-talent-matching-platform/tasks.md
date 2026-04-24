# Tasks: Manual Review AI Evidence Prepopulation

**Input**: Design documents from `/specs/001-talent-matching-platform/`  
**Prerequisites**: `plan.md`, `spec.md`, `research.md`, `data-model.md`, `contracts/manual-review-prepopulation.md`, `quickstart.md`

**Tests**: Include targeted regression tests for this update because US7 independent test criteria explicitly require verification of AI score + evidence prepopulation behavior.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Prepare regression fixtures and test scaffolding for both stacks.

- [ ] T001 [US7] Create Stack B scoring parser fixture samples in dotnet/tests/Application.Tests/Fixtures/scoring-parser-fixtures.json
- [ ] T002 [P] [US7] Create Stack A manual review prepopulation fixture samples in tests/unit/fixtures/manual-review-prepopulation-fixtures.json
- [ ] T003 [P] [US7] Add test helper for loading JSON fixtures in dotnet/tests/Application.Tests/TestFixtureLoader.cs

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Introduce shared prepopulation result shape and mismatch tracking used by all US7 changes.

**⚠️ CRITICAL**: No US7 implementation work should start until this phase is complete.

- [ ] T004 [US7] Extend Stack A prepopulation contract with `mismatchedCategories` in src/lib/stackb-scoring.ts
- [ ] T005 [P] [US7] Add mismatch warning view-model state to Stack A manual review state types in src/types/index.ts
- [ ] T006 [US7] Add Stack B manual review mismatch state fields in dotnet/src/Web.Client/Pages/ManualReview.razor
- [ ] T007 [P] [US7] Add parser helper methods for case-insensitive property access in dotnet/src/Application/Scoring/Commands/ScoreApplicationCommand.cs

**Checkpoint**: Shared structures and helper primitives are ready.

---

## Phase 3: User Story 7 - Recruiter performs a manual review (Priority: P3)

**Goal**: Restore and harden first-open AI prepopulation so each rubric category shows both AI score and evidence snippets, with clear mismatch handling.

**Independent Test**: Open manual review for an application with completed scoring and no saved review; verify each rubric category is prefilled with averaged AI score and evidence bullets; save and reload to confirm persisted values take precedence; verify mismatch and no-scoring cases follow US7 scenarios 7 and 8.

### Tests for User Story 7

- [ ] T008 [P] [US7] Add Stack B regression tests for evidence parsing variants in dotnet/tests/Application.Tests/ScoreApplicationCommandEvidenceParsingTests.cs
- [ ] T009 [P] [US7] Add Stack A regression tests for evidence collection and mismatch reporting in tests/unit/stackb-scoring.test.ts
- [ ] T010 [P] [US7] Add Stack B manual review prepopulation tests for first-open, mismatch, and no-scoring behavior in dotnet/tests/Application.Tests/ManualReviewPrepopulationTests.cs

### Implementation for User Story 7

- [ ] T011 [US7] Fix case-insensitive `evidence` array extraction in dotnet/src/Application/Scoring/Commands/ScoreApplicationCommand.cs
- [ ] T012 [US7] Fix category/snippet field-resolution heuristics for array-of-object outputs in dotnet/src/Application/Scoring/Commands/ScoreApplicationCommand.cs
- [ ] T013 [US7] Enforce non-empty evidence snippet filtering before `EvidenceCitationsJson` serialization in dotnet/src/Application/Scoring/Commands/ScoreApplicationCommand.cs
- [ ] T014 [US7] Deduplicate and trim evidence snippets during Stack B prepopulation in dotnet/src/Web.Client/Pages/ManualReview.razor
- [ ] T015 [US7] Implement per-category mismatch tracking and warning rendering in dotnet/src/Web.Client/Pages/ManualReview.razor
- [ ] T016 [US7] Preserve saved manual review precedence over AI prepopulation in dotnet/src/Web.Client/Pages/ManualReview.razor
- [ ] T017 [US7] Implement explicit Stack B banner behavior in dotnet/src/Web.Client/Pages/ManualReview.razor: show AI prepopulation banner with aggregated score/variance when AI prepopulation is active, and hide the banner when no scoring runs exist
- [ ] T018 [US7] Implement `mismatchedCategories` computation in Stack A prepopulation builder in src/lib/stackb-scoring.ts
- [ ] T019 [US7] Render per-category mismatch warnings in Stack A manual review UI in src/components/ManualReviewView.tsx
- [ ] T020 [US7] Ensure no-scoring case leaves rubric comments unpopulated and banner hidden in src/components/ManualReviewView.tsx

**Checkpoint**: US7 behavior matches FR-014 and FR-026 across both stacks.

---

## Phase 4: Polish & Cross-Cutting Concerns

**Purpose**: Validate parity, docs, and execution quality.

- [ ] T022 [P] [US7] Validate manual-review verification steps and expected outcomes in specs/001-talent-matching-platform/quickstart.md
- [ ] T023 [US7] Run Stack A regression test target for manual review prepopulation in tests/unit/stackb-scoring.test.ts
- [ ] T024 [US7] Run Stack B regression test target for parser/prepopulation in dotnet/tests/Application.Tests/ScoreApplicationCommandEvidenceParsingTests.cs
- [ ] T021 [P] [US7] Update implemented status for CHK056/CHK057 in specs/001-talent-matching-platform/checklists/parity.md after T023 and T024 pass
- [ ] T025 [US7] Record final implementation notes in specs/001-talent-matching-platform/research.md

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: No dependencies.
- **Phase 2 (Foundational)**: Depends on Phase 1; blocks US7 implementation.
- **Phase 3 (US7)**: Depends on Phase 2 completion.
- **Phase 4 (Polish)**: Depends on Phase 3 completion.

### User Story Dependencies

- **US7 (P3)**: Can be delivered independently once foundational tasks are complete.

### Within User Story 7

- Regression tests (T008-T010) MUST be authored and verified failing before implementation tasks (T011-T020).
- Parser fixes (T011-T013) should complete before Blazor prepopulation UI updates (T014-T017).
- Stack A prepopulation logic (T018) should complete before Stack A UI warnings (T019-T020).

---

## Parallel Opportunities

- Setup tasks T002 and T003 can run in parallel with T001.
- Foundational tasks T005 and T007 can run in parallel with T004/T006.
- US7 test tasks T008-T010 can run in parallel.
- US7 implementation can split by stack after parser primitives are in place:
  - Stack B parser path: T011-T013
  - Stack B UI path: T014-T017
  - Stack A logic/UI path: T018-T020
- Polish task T022 can run in parallel with test execution tasks T023-T024.

### Parallel Example: User Story 7

```bash
# Parallel tests
Task: T008 dotnet/tests/Application.Tests/ScoreApplicationCommandEvidenceParsingTests.cs
Task: T009 tests/unit/stackb-scoring.test.ts
Task: T010 dotnet/tests/Application.Tests/ManualReviewPrepopulationTests.cs

# Parallel implementation tracks after foundational completion
Task: T011-T013 dotnet/src/Application/Scoring/Commands/ScoreApplicationCommand.cs
Task: T018-T020 src/lib/stackb-scoring.ts + src/components/ManualReviewView.tsx
```

---

## Implementation Strategy

### MVP First (US7 only)

1. Complete Phase 1 and Phase 2.
2. Complete US7 tests and implementation (Phase 3).
3. Validate with quickstart manual-review verification flow.
4. Ship regression fix.

### Incremental Delivery

1. Parser reliability fix in Stack B (T011-T013).
2. Prepopulation UI parity and warnings (T014-T020).
3. Final hardening, parity checklist, and regression test execution (Phase 4).
