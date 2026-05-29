# Tasks: Manual Review AI Evidence Prepopulation

**Input**: Design documents from `/specs/001-talent-matching-platform/`
**Prerequisites**: `plan.md` (required), `spec.md` (required — US7 scenarios 6-8, FR-014, FR-026), `research.md` (root cause: Q1-Q5), `data-model.md` (EvidenceCitation schema), `contracts/manual-review-prepopulation.md` (rules PR-1–PR-4), `quickstart.md` (verification flow)

**Tests**: YES — research.md Decision #4 mandates test-then-fix: write regression-capturing tests FIRST, verify they FAIL against the current code, then implement fixes. US7 independent test criteria also explicitly require verification of AI score + evidence prepopulation behavior.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing. This iteration is scoped exclusively to US7 (FR-014, FR-026) per the spec Iteration Scope Amendment.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US7 only for this iteration)
- Include exact file paths in descriptions

## Path Conventions

- **Stack A (React/TypeScript/Express)**: `src/`, `server/`, `tests/` at repository root
- **Stack B (.NET 9 Blazor WASM)**: `dotnet/src/`, `dotnet/tests/` at repository root

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create test fixtures and scaffolding for both stacks, representing the LLM response variants that trigger the regression.

- [X] T001 Create Stack B scoring parser JSON fixture file at dotnet/tests/Application.Tests/Fixtures/scoring-parser-fixtures.json containing: (a) nested-object format with lowercase `"evidence"`, (b) nested-object format with capitalised `"Evidence"` (regression case per research.md Q1 defect 1), (c) array-of-objects format with category-name-first field order, (d) array-of-objects format with evidence-before-category field order (regression case per research.md Q1 defect 2), (e) mixed-case property names, (f) empty/null evidence fields
- [X] T002 [P] Create Stack A manual review prepopulation fixture file at tests/unit/fixtures/manual-review-prepopulation-fixtures.json containing: (a) scoring runs with matched category names, (b) scoring runs with mismatched category names (substring-match case), (c) scoring runs with completely unmatched categories, (d) empty scoring runs array, (e) scoring runs with empty evidence arrays, (f) existing saved manual review data (PR-2 precedence case)
- [X] T003 [P] Add test fixture loader helper in dotnet/tests/Application.Tests/TestFixtureLoader.cs that reads embedded JSON fixtures by name and deserializes to `JsonElement` for parser tests

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Introduce the `mismatchedCategories` contract extension and case-insensitive parser helpers needed by all US7 implementation tasks.

**⚠️ CRITICAL**: No US7 implementation work can begin until this phase is complete.

- [X] T004 Add `mismatchedCategories: string[]` property to `StackBPrepopulationResult` interface in src/lib/stackb-scoring.ts (research.md Decision #3) — initialise as empty array in `buildStackBManualReviewPrepopulation` return paths
- [X] T005 [P] Add `mismatchedCategories` state variable and corresponding setter in src/components/ManualReviewView.tsx (reads from `StackBPrepopulationResult.mismatchedCategories` in the prepopulation effect)
- [X] T006 [P] Add `List<string> mismatchedCategories` field and `bool hasPerCategoryMismatch` computed property to the ManualReview page state in dotnet/src/Web.Client/Pages/ManualReview.razor (near existing `aiScoringMismatch` field at line ~180)
- [X] T007 [P] Add private helper method `TryGetPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)` in dotnet/src/Application/Scoring/Commands/ScoreApplicationCommand.cs that enumerates `obj.EnumerateObject()` and matches property names with `StringComparison.OrdinalIgnoreCase` — returns first match

**Checkpoint**: Foundation ready — `mismatchedCategories` contract, UI state, and parser helper are in place.

---

## Phase 3: User Story 7 — Recruiter performs a manual review with AI prepopulation (Priority: P3) 🎯 MVP

**Goal**: Restore and spec-harden first-open AI prepopulation so each rubric category shows both the AI-assigned score (averaged across runs) AND evidence snippets (direct CV quotes) in its notes field — per FR-014, US7 scenario 6, and contract rules PR-1 through PR-4. Fix the two `ParseSingleRun` regressions in Stack B. Harden Stack A mismatch reporting.

**Independent Test**: Open manual review for an application with completed scoring and no saved review → verify each rubric category shows AI score AND evidence bullets in its notes field. Save → reload → confirm persisted values take precedence (PR-2). Test with mismatched category names → verify per-category mismatch warning (US7-7). Test with no scoring runs → verify no banner, empty inputs (US7-8).

### Tests for User Story 7

> **NOTE: Write these tests FIRST. They MUST FAIL against current code before implementation begins (research.md Decision #4).**

- [X] T008 [P] [US7] Write Stack B `ParseSingleRun` evidence extraction unit tests in dotnet/tests/Application.Tests/ScoreApplicationCommandEvidenceParsingTests.cs — test cases: (a) nested-object with lowercase `"evidence"` → citations extracted, (b) nested-object with capitalised `"Evidence"` → citations extracted (MUST FAIL before T011 fix), (c) array-of-objects with category-first field order → correct category+snippet, (d) array-of-objects with evidence-first field order → correct category+snippet (MUST FAIL before T012 fix), (e) empty/null evidence fields → empty citations, not crash, (f) non-JSON response → graceful failure with empty citations
- [X] T009 [P] [US7] Write Stack A `collectEvidenceByRubricCategory` and `buildStackBManualReviewPrepopulation` unit tests in tests/unit/stackb-scoring.test.ts — test cases: (a) matched categories → evidence grouped correctly, (b) substring-match categories → evidence matched via fuzzy tier 2, (c) word-overlap categories (≥40%) → evidence matched via fuzzy tier 3, (d) completely unmatched categories → `mismatchedCategories` populated (MUST FAIL before T018 fix), (e) empty evidence arrays → no crash, empty comments, (f) existing saved review → `aiPrePopulated: false`, no overwrite (PR-2), (g) no scoring runs → `aiPrePopulated: false`, `aiScoringMismatch: false` (PR-4), (h) mixed matched/unmatched → partial prepopulation + partial mismatch
- [X] T010 [P] [US7] Write Stack B `PrePopulateFromAiAsync` integration-level tests in dotnet/tests/Application.Tests/ManualReviewPrepopulationTests.cs — test cases: (a) first-open with valid evidence → scores + evidence comment pre-filled (PR-1), (b) existing saved review → AI values NOT applied (PR-2), (c) scoring runs with unmatched categories → `mismatchedCategories` populated, warning shown (PR-3), (d) no scoring runs → no banner, empty inputs (PR-4), (e) evidence deduplication → duplicate snippets across runs collapsed to unique set

### Implementation for User Story 7 — Stack B Parser Fixes

- [X] T011 [US7] Fix case-sensitive `TryGetProperty("evidence", ...)` calls in `ParseSingleRun` at dotnet/src/Application/Scoring/Commands/ScoreApplicationCommand.cs — replace all `TryGetProperty("evidence", out var ...)` calls (lines ~204, ~273, ~322) with `TryGetPropertyCaseInsensitive(element, "evidence", out var ...)` from T007 so that `"Evidence"`, `"EVIDENCE"`, and `"evidence"` are all matched (research.md Q1 defect 1)
- [X] T012 [US7] Fix position-dependent field extraction in `ExtractCategoryName` and `ExtractStringField` at dotnet/src/Application/Scoring/Commands/ScoreApplicationCommand.cs — change `ExtractCategoryName` (line ~515) to prefer properties whose name contains "category", "name", or "label" (case-insensitive) before falling back to first-short-string heuristic; change `ExtractStringField` (line ~499) to prefer properties whose name contains "evidence", "justification", "rationale", or "snippet" before falling back to longest-string heuristic (research.md Q1 defect 2)
- [X] T013 [US7] Add non-empty snippet guard before appending to the evidence citations list in `ParseSingleRun` at dotnet/src/Application/Scoring/Commands/ScoreApplicationCommand.cs — filter out citations where `snippet` is null, empty, or whitespace-only before serializing to `EvidenceCitationsJson` (data-model.md: "MUST NOT be empty — citations with empty snippets are discarded")

### Implementation for User Story 7 — Stack B Prepopulation UI

- [X] T014 [US7] Harden evidence deduplication in `PrePopulateFromAiAsync` at dotnet/src/Web.Client/Pages/ManualReview.razor — after collecting evidence snippets from all N scoring runs per category (line ~330-356), deduplicate by normalised string comparison (trim + case-insensitive) before inserting into the comment field
- [X] T015 [US7] Implement per-category mismatch tracking in `PrePopulateFromAiAsync` at dotnet/src/Web.Client/Pages/ManualReview.razor — after the category-matching loop, collect rubric category names that had zero matched citations AND zero matched scores into the `mismatchedCategories` list (from T006); set `hasPerCategoryMismatch = mismatchedCategories.Count > 0`
- [X] T016 [US7] Add per-category mismatch warning rendering in the rubric form section of dotnet/src/Web.Client/Pages/ManualReview.razor — for each category in `mismatchedCategories`, display an amber inline warning below the category score input: "AI scoring data could not be matched to this category. You may need to re-score." (contract PR-3)
- [X] T017 [US7] Verify and harden banner behavior in dotnet/src/Web.Client/Pages/ManualReview.razor — (a) when `aiPrePopulated == true`: show blue banner with aggregated AI score + variance (contract PR-1), (b) when `aiScoringMismatch == true && !aiPrePopulated`: show amber global mismatch banner (contract PR-3), (c) when no scoring runs exist: hide both banners entirely (contract PR-4), (d) when saved review exists: hide AI banner, show loaded-from-saved indicator (contract PR-2)

### Implementation for User Story 7 — Stack A Hardening

- [X] T018 [US7] Implement `mismatchedCategories` computation in `buildStackBManualReviewPrepopulation` at src/lib/stackb-scoring.ts — after the score-averaging loop, compute which rubric category names had zero matched AI scores AND zero matched evidence; populate `mismatchedCategories` array in the return value; set `aiScoringMismatch = true` when `mismatchedCategories.length > 0 && mismatchedCategories.length === rubricCategories.length` (total mismatch), keep partial prepopulation active when only some categories mismatch
- [X] T019 [US7] Render per-category mismatch warnings in src/components/ManualReviewView.tsx — for each category name in `mismatchedCategories`, display an amber inline warning below the category score input matching the Stack B warning text from T016; update the existing global mismatch banner (line ~349) to also list the specific unmatched category names
- [X] T020 [US7] Verify no-scoring-runs path in src/components/ManualReviewView.tsx — confirm that when `aiPrePopulated === false && aiScoringMismatch === false` (PR-4 case), all rubric category inputs are empty/zero, all comment fields are empty, and neither the blue AI banner nor the amber mismatch banner is rendered

**Checkpoint**: At this point, US7 behavior matches FR-014 and FR-026 across both stacks. All contract rules PR-1 through PR-4 are implemented. Per-category mismatch warnings are rendered in both stacks.

---

## Phase 4: Polish & Cross-Cutting Concerns

**Purpose**: Run tests, validate cross-stack parity, update documentation.

- [X] T021 Run Stack B unit tests via `dotnet test dotnet/TalentMatch.slnx --filter "FullyQualifiedName~ScoreApplicationCommandEvidenceParsingTests|FullyQualifiedName~ManualReviewPrepopulationTests"` and verify all T008/T010 tests pass after implementation
- [X] T022 [P] Run Stack A unit tests via `npx vitest run tests/unit/stackb-scoring.test.ts` and verify all T009 tests pass after implementation
- [X] T023 [P] Validate quickstart manual-review verification steps in specs/001-talent-matching-platform/quickstart.md — walk through all 5 verification steps and confirm expected results match actual behavior for both stacks
- [X] T024 Update parity checklist items CHK056/CHK057 (if they exist) in specs/001-talent-matching-platform/checklists/parity.md to reflect implemented status after T021 and T022 pass
- [X] T025 Record implementation notes in specs/001-talent-matching-platform/research.md — document which specific lines were changed in each file, any additional edge cases discovered during implementation, and final test pass/fail status

---

## Phase 5: Clarification Implementation — HumanEdited Gate (Option 1)

**Purpose**: Implement the 2026-04-24 clarification that AI prepopulation is skipped only when
manual review has been genuinely human-edited, using a persisted `humanEdited` field with parity
across both stacks and both data providers (SQLite and Azure SQL).

- [X] T026 [US7] Add `humanEdited` to manual-review domain/contracts in both stacks: Stack A shared types (`src/types`) and Stack B domain/entity + request/response DTOs (`dotnet/src/Domain`, `dotnet/src/Web.Server`) with default `false`
- [X] T027 [P] [US7] Implement Stack A persistence for `humanEdited` in both providers: add `HumanEdited` column handling in `server/storage/schema.sql` and repository read/write mapping in `server/storage/repos/application-repo.ts` (SQLite + Azure SQL query paths)
- [X] T028 [P] [US7] Implement Stack B persistence for `humanEdited` in both providers: EF model mapping in `dotnet/src/Infrastructure/Persistence/AppDbContext.cs`, repository read/write in `dotnet/src/Infrastructure/Persistence/Repositories/ApplicationRepository.cs`, and migration/model updates for Azure SQL + SQLite compatibility
- [X] T029 [US7] Update manual-review API endpoints in both stacks (`server/routes/applications.ts`, `dotnet/src/Web.Server/Endpoints/ApplicationsEndpoints.cs`) so `humanEdited` is returned and accepted consistently
- [X] T030 [US7] Update prepopulation gates in both UIs (`src/components/ManualReviewView.tsx`, `dotnet/src/Web.Client/Pages/ManualReview.razor`) to skip AI prepopulation only when `humanEdited == true`; do not skip when content is AI-prepopulated and `humanEdited == false`
- [X] T031 [US7] Ensure save logic flips `humanEdited` from `false` to `true` only when reviewer makes real edits to AI-prepopulated content (score/comment/overall comment deltas), and remains unchanged when no user edit occurred
- [X] T032 [P] [US7] Add tests for gate semantics in both stacks: (a) AI-only saved content + `humanEdited=false` still prepopulates, (b) after user edit + save sets `humanEdited=true`, (c) `humanEdited=true` skips prepopulation, (d) behavior identical in SQLite and Azure SQL-backed runs
- [X] T033 [US7] Run cross-stack parity verification for `humanEdited`: create/edit/reload manual reviews from each stack against each provider and confirm identical persisted values and prepopulation behavior

**Checkpoint**: Clarification is complete when `humanEdited` is the sole skip gate and parity is
verified across Stack A/Stack B and SQLite/Azure SQL.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion — BLOCKS all user story work
- **User Story 7 (Phase 3)**: Depends on Foundational phase completion
- **Polish (Phase 4)**: Depends on all Phase 3 tasks being complete

### User Story Dependencies

- **User Story 7 (P3)**: Only story in scope for this iteration. Can start after Foundational (Phase 2). No dependencies on other user stories.

### Within User Story 7

- **Tests FIRST**: Regression tests (T008, T009, T010) MUST be written and verified FAILING before any implementation task begins (research.md Decision #4)
- **Parser before UI**: Stack B parser fixes (T011 → T012 → T013) must complete before Stack B prepopulation UI updates (T014–T017) because the UI reads from the corrected `EvidenceCitationsJson`
- **Logic before rendering**: Stack A prepopulation logic (T018) must complete before Stack A UI warnings (T019, T020)
- **Stack independence**: Stack B parser path (T011–T013) and Stack A hardening path (T018) can proceed in parallel once tests are written

### Parallel Opportunities

- All Setup tasks marked [P] can run in parallel (T002, T003 alongside T001)
- All Foundational tasks marked [P] can run in parallel (T005, T006, T007 alongside T004)
- All test-writing tasks can run in parallel (T008, T009, T010)
- Once tests are written, implementation splits into two independent tracks:
  - **Stack B track**: T011 → T012 → T013 → T014 → T015 → T016 → T017
  - **Stack A track**: T018 → T019 → T020
- Polish tasks T022 and T023 can run in parallel with T021

---

## Parallel Example: User Story 7

```text
# Phase 3 — Launch all regression tests in parallel (different files, different stacks):
T008: dotnet/tests/Application.Tests/ScoreApplicationCommandEvidenceParsingTests.cs
T009: tests/unit/stackb-scoring.test.ts
T010: dotnet/tests/Application.Tests/ManualReviewPrepopulationTests.cs

# Phase 3 — After tests written, two parallel implementation tracks:
Track A (Stack B): T011 → T012 → T013 → T014 → T015 → T016 → T017
Track B (Stack A): T018 → T019 → T020

# Phase 4 — Parallel validation:
T021: dotnet test (Stack B)
T022: vitest run (Stack A)
T023: quickstart walkthrough (both stacks)
```

---

## Implementation Strategy

### MVP First (US7 Parser + Evidence Fix)

1. Complete Phase 1: Setup (fixtures)
2. Complete Phase 2: Foundational (contract extension + helper)
3. Write regression tests (T008–T010) — verify they FAIL
4. Fix Stack B parser (T011–T013) — verify T008 tests now PASS
5. **STOP and VALIDATE**: Run `dotnet test` for Stack B parser tests
6. Fix Stack B UI (T014–T017) — verify T010 tests now PASS
7. Fix Stack A (T018–T020) — verify T009 tests now PASS
8. **VALIDATE**: Run quickstart manual-review verification flow

### Incremental Delivery

1. Setup + Foundational → contract and helpers ready
2. Regression tests → failing baseline captured (proof the bug exists)
3. Stack B parser fix (T011–T013) → evidence now correctly extracted and stored
4. Stack B UI fix (T014–T017) → evidence now displayed, mismatch warnings shown
5. Stack A hardening (T018–T020) → mismatchedCategories populated, per-category warnings
6. Polish (T021–T025) → tests green, parity confirmed, docs updated
7. Each step adds value and is independently verifiable

### Parallel Team Strategy

With two developers:

1. Team completes Setup + Foundational together
2. Once Foundational is done:
   - Developer A: Stack B track (T008 → T011–T017 → T021)
   - Developer B: Stack A track (T009 → T018–T020 → T022)
3. Both run T023 (quickstart) together to validate cross-stack parity
4. Either developer handles T024–T025 (docs)

---

## Notes

- Note: The 2026-04-24 clarification supersedes the earlier "no schema migration" assumption for this iteration; `humanEdited` requires additive schema/mapping updates with SQLite + Azure SQL parity.
- `dotnet/tests/Application.Tests/` project already exists with 6 test files — new test files go there
- `tests/unit/` directory already exists — new test file goes there
- Stack A `collectEvidenceByRubricCategory` is functionally correct (research.md Q2) — T018 only extends it with `mismatchedCategories`
- Stack B `MatchCategory` in ManualReview.razor and Stack A `matchCategoryToRubric` implement the same 3-tier algorithm (research.md Q4) — no divergence fix needed
- Contract rules PR-1 through PR-4 from `contracts/manual-review-prepopulation.md` map directly to US7 scenarios 6, 7, 8 and FR-014
