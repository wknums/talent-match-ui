# Tasks: Recruiter Analytics Dashboard

**Input**: Design documents from `/specs/005-recruiter-analytics/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅, quickstart.md ✅

**Tests**: Included — plan.md explicitly lists test files (`tests/unit/analytics.test.ts`, `tests/integration/api.test.ts`, `dotnet test`).

**Organization**: Tasks grouped by user story. Stack A frontend already exists (`AnalyticsView.tsx` with mock data). This plan covers: Stack A backend endpoints + `api-real.ts` wiring, and full Stack B (.NET Blazor) implementation. US5 (Server-Side Computation) and US6 (Access Control) are absorbed into the Foundational phase since they are blocking prerequisites for all UI stories to function with real data.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: No new project initialization required — both Stack A (Express/React) and Stack B (.NET Blazor) projects already exist and are functional.

_(No tasks — proceed to Phase 2)_

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Domain entities, backend API endpoints with RBAC, and API client wiring for both stacks. Encompasses US5 (Server-Side Analytics Computation) and US6 (Server-Side Access Control) since these are blocking prerequisites for all UI stories.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Stack B Domain Entities

- [x] T001 [P] Create RecruiterAnalytics record in dotnet/src/Domain/Entities/RecruiterAnalytics.cs — immutable C# record with RecruiterId, RecruiterName, Department, ApplicationsInQueue, ManualReviewsPerformed, ShortlistRecommendations, AverageProcessingTime (double?), ActiveJobs per data-model.md
- [x] T002 [P] Create DepartmentAnalytics record in dotnet/src/Domain/Entities/DepartmentAnalytics.cs — immutable C# record with Department, TotalRecruiters, ApplicationsInQueue, ManualReviewsPerformed, ShortlistRecommendations, ActiveJobs, IReadOnlyList\<RecruiterAnalytics\> Recruiters per data-model.md

### Stack A Backend (Express)

- [x] T003 Implement analytics computation helper in server/routes/stats.ts — function that loads jobs from jobs:all, iterates jobs:{jobId}:applications, loads users from users:all via StorageProvider, and computes per-recruiter metrics: applicationsInQueue (status=Queued), manualReviewsPerformed (status=NeedsManualReview or flagged=true), shortlistRecommendations (finalDecision=Eligible), activeJobs (job status Active/Processing), averageProcessingTime (createdAt-to-completion or undefined) per research.md R1/R3/R6. Follow-up required: Stack A still stores username in `createdBy`, so recruiter ownership normalization is not yet complete.
- [x] T004 Add GET /api/stats/recruiters route in server/routes/stats.ts — protected with requireRole('admin', 'recruiter') from server/middleware/rbac.ts, apply department scoping (admin=all recruiters, recruiter=only req.user.department), return RecruiterAnalytics[] per contracts/get-recruiter-analytics.md (401 if unauthenticated, 403 if unauthorized role)
- [x] T005 Add GET /api/stats/departments route in server/routes/stats.ts — protected with requireRole('admin', 'recruiter'), group recruiter records by department, sum aggregate metrics, apply department scoping (admin=all, recruiter=own dept), return DepartmentAnalytics[] per contracts/get-department-analytics.md
- [x] T006 [P] Implement getRecruiterAnalytics() in src/lib/api-real.ts — replace empty array stub with fetch call to GET /api/stats/recruiters, return RecruiterAnalytics[]
- [x] T007 [P] Implement getDepartmentAnalytics() in src/lib/api-real.ts — replace empty array stub with fetch call to GET /api/stats/departments, return DepartmentAnalytics[]

### Stack B Backend (.NET)

- [x] T008 Implement GetRecruiterAnalyticsQuery with MediatR handler in dotnet/src/Application/Analytics/Queries/GetRecruiterAnalyticsQuery.cs — query accepts caller Role and Department, handler aggregates from Job + Application + User entities via AppDbContext, applies department scoping (admin=all, recruiter=own dept), returns List\<RecruiterAnalytics\> per research.md R4
- [x] T009 [P] Implement GetDepartmentAnalyticsQuery with MediatR handler in dotnet/src/Application/Analytics/Queries/GetDepartmentAnalyticsQuery.cs — query accepts caller Role and Department, handler groups RecruiterAnalytics by department, sums aggregate metrics, applies department scoping, returns List\<DepartmentAnalytics\> per research.md R4
- [x] T010 Create AnalyticsEndpoints.cs in dotnet/src/Web.Server/Endpoints/AnalyticsEndpoints.cs — map GET /api/stats/recruiters and GET /api/stats/departments as minimal API endpoints, require authorization for admin/recruiter roles (403 for other roles, 401 for unauthenticated), extract caller role and department from authenticated user claims, dispatch to MediatR queries
- [x] T011 Register analytics query handlers and endpoint mappings in dotnet/src/Application/DependencyInjection.cs and dotnet/src/Web.Server/ startup — ensure MediatR discovers Analytics query handlers and AnalyticsEndpoints are mapped in the route builder

**Checkpoint**: Foundation ready — all backend endpoints are operational with RBAC in both stacks. Stack A frontend works end-to-end with `API_MODE=real`, but Stack A recruiter ownership normalization still needs a parity correction.

---

## Phase 3: User Story 4 — Navigate to and from Analytics (Priority: P1)

**Goal**: Enable navigation to the analytics view from the dashboard and back in Stack B. Stack A navigation already exists in `AnalyticsView.tsx` and `DashboardView.tsx`.

**Independent Test**: Verify "View Analytics" button appears for admin/recruiter roles (not business_panel) in Stack B dashboard, navigates to analytics page, and "← Back to Dashboard" returns to dashboard.

### Implementation for User Story 4

- [x] T012 [US4] Create Analytics.razor page shell with @page "/analytics" route, page title "Recruiter Analytics", and base layout structure in dotnet/src/Web.Client/Pages/Analytics.razor
- [x] T013 [P] [US4] Add "View Analytics" navigation button visible only to admin and recruiter roles (hidden for business_panel) in the Stack B dashboard page at dotnet/src/Web.Client/Pages/ per FR-001
- [x] T014 [US4] Add "← Back to Dashboard" navigation link at top of dotnet/src/Web.Client/Pages/Analytics.razor that routes to the main dashboard per FR-002

**Checkpoint**: At this point, navigation between dashboard and analytics page works in both stacks for authorized roles.

---

## Phase 4: User Story 1 — View Recruiter Performance Summary (Priority: P1) 🎯 MVP

**Goal**: Display four summary stat cards showing aggregated totals for Applications in Queue, Manual Reviews, Shortlist Recommendations, and Active Jobs. Admin sees all data; recruiter sees department-scoped data (server returns pre-scoped data per research.md R5).

**Independent Test**: Navigate to analytics page as admin — verify four stat cards display aggregated metrics. Log in as recruiter — verify stat cards show department-scoped totals. Test skeleton loading and zero-data states.

### Implementation for User Story 1

- [x] T015 [US1] Implement data fetching in Analytics.razor — call GET /api/stats/recruiters on page load via HttpClient, deserialize to List\<RecruiterAnalytics\>, compute summary totals by summing all returned recruiter records' metrics
- [x] T016 [US1] Add four summary stat cards (Applications in Queue, Manual Reviews, Shortlist Recommendations, Active Jobs) to dotnet/src/Web.Client/Pages/Analytics.razor displaying computed totals per FR-003/FR-016
- [x] T017 [US1] Add skeleton loading states for page title, stat cards, and table area in dotnet/src/Web.Client/Pages/Analytics.razor — show placeholder elements while API call is in-flight per FR-008
- [x] T018 [US1] Handle empty/zero data state — stat cards display zero values gracefully when API returns empty array in dotnet/src/Web.Client/Pages/Analytics.razor per acceptance scenario US1-4

**Checkpoint**: At this point, User Story 1 is implemented — summary stat cards are visible on analytics page in both stacks.

---

## Phase 5: User Story 2 — Browse Analytics by Recruiter (Priority: P1)

**Goal**: Display a table of individual recruiter metrics with columns: Recruiter, Department, Applications in Queue, Manual Reviews, Shortlist Recs, Active Jobs, Avg. Time (hrs). Includes department filter dropdown, role-based data scoping (handled server-side), and empty state.

**Independent Test**: Navigate to analytics page, confirm "By Recruiter" tab is active by default, verify table displays all recruiter records with correct columns. Test department filter dropdown, empty state, and recruiter role sees only own department.

### Implementation for User Story 2

- [x] T019 [US2] Add tabbed interface with "By Recruiter" (default active) and "By Department" tabs to dotnet/src/Web.Client/Pages/Analytics.razor below the stat cards per FR-004
- [x] T020 [US2] Implement "By Recruiter" data table in dotnet/src/Web.Client/Pages/Analytics.razor with columns: Recruiter, Department, Applications in Queue, Manual Reviews, Shortlist Recs, Active Jobs, Avg. Time (display hours or "—" when AverageProcessingTime is null) per FR-005
- [x] T021 [US2] Add department filter dropdown above the recruiter table in dotnet/src/Web.Client/Pages/Analytics.razor — populate from distinct departments in loaded data, filter table client-side on selection (instant, no API call) per FR-006/SC-002
- [x] T022 [US2] Add "No recruiters found" empty state message when no data matches current department filter in dotnet/src/Web.Client/Pages/Analytics.razor per FR-009

**Checkpoint**: At this point, User Stories 1 AND 2 are implemented — stat cards and recruiter table render in both stacks, with Stack A metrics still requiring ownership validation.

---

## Phase 6: User Story 3 — Browse Analytics by Department (Priority: P2)

**Goal**: Display aggregated metrics grouped by department. Each department shown as a card with summary stats (In Queue, Reviews, Shortlisted, Active Jobs) and a nested table of individual recruiters. Admin sees all departments; recruiter sees own department only.

**Independent Test**: Switch to "By Department" tab, verify each department appears as a card with correct summary stats and nested recruiter table. Test with admin (all departments) and recruiter (own department only). Test zero-recruiter department displays correctly.

### Implementation for User Story 3

- [x] T023 [US3] Implement department data fetching — call GET /api/stats/departments on tab switch (or alongside recruiter fetch) in Analytics.razor, deserialize to List\<DepartmentAnalytics\>
- [x] T024 [US3] Implement "By Department" tab content in dotnet/src/Web.Client/Pages/Analytics.razor — render a card per department showing summary stats (Applications in Queue, Manual Reviews, Shortlist Recommendations, Active Jobs) and a nested table of individual recruiters per FR-007
- [x] T025 [US3] Handle edge cases in department view in dotnet/src/Web.Client/Pages/Analytics.razor — zero-recruiter departments show zero-value stats with empty recruiter table, recruiter role sees only own department card, per acceptance scenarios US3-2 and US3-3

**Checkpoint**: All user stories are implemented in both stacks, but Stack A recruiter ownership normalization remains open before parity can be treated as complete.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Testing, validation, and end-to-end verification across both stacks

- [x] T026 [P] Create analytics computation unit tests in tests/unit/analytics.test.ts — test recruiter metric aggregation: applicationsInQueue counting (status=Queued), manualReviewsPerformed counting (NeedsManualReview/flagged), shortlistRecommendations (Eligible), activeJobs (Active/Processing status), averageProcessingTime calculation (and undefined when no completions), edge cases (no jobs, no applications, recruiter without department excluded)
- [x] T027 [P] Add analytics endpoint integration tests in tests/integration/api.test.ts — test GET /api/stats/recruiters: 200 with valid schema for admin, 200 with department-scoped data for recruiter, 403 for business_panel, 401 for unauthenticated; test GET /api/stats/departments: same auth matrix, verify aggregate sums match nested recruiters, verify totalRecruiters equals recruiters array length
- [x] T028 [P] Create Stack B analytics tests in dotnet/tests/ — test GetRecruiterAnalyticsQuery and GetDepartmentAnalyticsQuery handlers: correct metric aggregation, admin sees all recruiters, recruiter sees only own department, empty data returns empty array, department aggregates sum correctly
- [x] T029 Run quickstart.md validation for both stacks — execute manual testing steps: start Stack A with npm run dev, log in as admin/recruiter/business_panel, verify all 6 user story acceptance scenarios; start Stack B with dotnet run, repeat verification

---

## Phase 8: Stack A Parity Correction

**Purpose**: Correct the Stack A recruiter ownership mismatch discovered after implementation so parity can be re-verified.

- [ ] T030 Normalize Stack A recruiter ownership in `server/routes/stats.ts` and/or the Stack A job-creation path so recruiter analytics group jobs against the same recruiter identifier used by the user repository.
- [ ] T031 Extend Stack A analytics tests to cover the username-vs-userId ownership mismatch and prevent regressions once the normalization fix is applied.
- [ ] T032 Re-run quickstart/manual validation after the Stack A fix and only then restore the feature to parity-complete status.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — nothing to do
- **Foundational (Phase 2)**: No dependencies — can start immediately. BLOCKS all user stories
- **US4 Navigation (Phase 3)**: Depends on Foundational (Phase 2) completion
- **US1 Summary (Phase 4)**: Depends on US4 (Phase 3) — needs analytics page to render on
- **US2 Recruiter Table (Phase 5)**: Depends on US1 (Phase 4) — extends page with tabbed interface
- **US3 Department View (Phase 6)**: Depends on US2 (Phase 5) — uses tab structure from US2
- **Polish (Phase 7)**: Depends on all user stories being complete

### User Story Dependencies

- **US4 (P1)**: Can start after Foundational (Phase 2) — No dependencies on other stories
- **US1 (P1)**: Depends on US4 — Needs analytics page shell and data-fetching infrastructure
- **US2 (P1)**: Depends on US1 — Builds on the same page, introduces tab navigation
- **US3 (P2)**: Depends on US2 — Adds "By Department" tab to existing tab interface
- **US5+US6 (P2)**: Absorbed into Foundational — backend computation and RBAC are prerequisites

### Within Each User Story

- Domain entities before application queries
- Application queries before API endpoints
- API endpoints before frontend pages
- Core rendering before edge case handling
- Backend before frontend (server-scoped data drives UI)

### Parallel Opportunities

- T001, T002: .NET domain entities (different files, no dependencies)
- T006, T007: `api-real.ts` implementations (different functions in same file, independent stubs)
- T008, T009: .NET query handlers (different files, independent queries)
- T012, T013: Blazor page shell and dashboard navigation button (different files)
- T026, T027, T028: All test suites (different files, different stacks)

---

## Parallel Example: Foundational Phase

```bash
# Launch .NET domain entities together:
Task T001: "Create RecruiterAnalytics.cs in dotnet/src/Domain/Entities/"
Task T002: "Create DepartmentAnalytics.cs in dotnet/src/Domain/Entities/"

# Launch api-real.ts implementations together:
Task T006: "Implement getRecruiterAnalytics() in src/lib/api-real.ts"
Task T007: "Implement getDepartmentAnalytics() in src/lib/api-real.ts"

# Launch .NET query handlers together:
Task T008: "GetRecruiterAnalyticsQuery in dotnet/src/Application/"
Task T009: "GetDepartmentAnalyticsQuery in dotnet/src/Application/"
```

## Parallel Example: Polish Phase

```bash
# Launch all test suites together:
Task T026: "Unit tests in tests/unit/analytics.test.ts"
Task T027: "Integration tests in tests/integration/api.test.ts"
Task T028: "Stack B tests in dotnet/tests/"
```

---

## Implementation Strategy

### MVP First (User Stories 4 + 1 Only)

1. Complete Phase 2: Foundational (CRITICAL — blocks all stories)
2. Complete Phase 3: US4 — Navigation (Stack B page shell)
3. Complete Phase 4: US1 — Summary Stat Cards
4. **STOP and VALIDATE**: Test stat cards display correctly for admin and recruiter roles in both stacks
5. Deploy/demo if ready — basic analytics visibility achieved

### Incremental Delivery

1. Complete Foundational → Backend ready, Stack A fully functional with `API_MODE=real`
2. Add US4 Navigation → Stack B analytics page accessible
3. Add US1 Summary → Stat cards visible in both stacks (MVP!)
4. Add US2 Recruiter Table → Detailed recruiter-level view with filtering
5. Add US3 Department View → Department-level aggregated analysis
6. Polish → Tests + quickstart validation
7. Each story adds value without breaking previous stories

### Parallel Team Strategy

With multiple developers:

1. Team completes Foundational together (or split: Dev A = Stack A backend, Dev B = Stack B backend)
2. Once Foundational is done:
   - Developer A: US4 → US1 → US2 → US3 (Stack B UI, sequential within Analytics.razor)
   - Developer B: T026, T027 (Stack A tests — can run in parallel with Stack B UI)
   - Developer C: T028 (Stack B tests — can run in parallel with Stack B UI)
3. All test suites can run in parallel after endpoints exist

---

## Notes

- [P] tasks = different files, no dependencies on incomplete tasks
- [Story] label maps task to specific user story for traceability
- Stack A frontend (`AnalyticsView.tsx`) already exists and is fully functional with mock data — no Stack A frontend changes needed
- US5 (Server-Side Computation) and US6 (Access Control) from spec.md are absorbed into Foundational phase as blocking prerequisites
- `averageProcessingTime` is `null`/`undefined` (not `0`) when no completed applications exist — UI displays "—"
- Recruiters without a `department` field are excluded from analytics results per edge case spec
- Server returns pre-scoped data based on caller role — client-side sum of returned data automatically reflects correct scope (research.md R5)
- Stack A parity is still blocked by recruiter-ownership normalization because jobs currently store username in `createdBy` while analytics group recruiters by `userId`
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
