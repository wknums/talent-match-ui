# Tasks: Talent Matching Platform

**Input**: Design documents from `/specs/001-talent-matching-platform/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅, quickstart.md ✅

**Tests**: Not explicitly requested in the feature specification — test tasks are omitted. Existing test infrastructure (Vitest for Stack A, xUnit for Stack B) is preserved and can be extended independently.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story. Both Stack A (React/TypeScript/Express) and Stack B (.NET/Blazor/Clean Architecture) are covered per story. This is a **brownfield** project — many files already exist and tasks focus on implementing or completing the required functionality.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **Stack A**: `src/` (React frontend), `server/` (Express backend)
- **Stack B**: `dotnet/src/Domain/`, `dotnet/src/Application/`, `dotnet/src/Infrastructure/`, `dotnet/src/Web.Server/`, `dotnet/src/Web.Client/`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Environment configuration, shared dependencies, and AWR auth infrastructure required by both stacks before any feature work can proceed.

- [ ] T001 Add AWR auth environment variables (`AWR_AUTH_MODE`, `AWR_API_KEY`, `AWR_AAD_ISSUER`, `AWR_AAD_AUDIENCE`) to `.env.example` and document in `specs/001-talent-matching-platform/quickstart.md` per FR-050
- [ ] T002 [P] Add `AWR_PLATFORM_API_ENDPOINT` environment variable to `.env.example` and document scoring-mode behaviour (sequential vs platform) in `specs/001-talent-matching-platform/quickstart.md` per FR-061
- [ ] T003 [P] Verify both stacks build cleanly — run `npm install && npm run build` for Stack A and `dotnet build dotnet/TalentMatch.slnx` for Stack B; resolve any dependency or compilation errors

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: New domain entities, enums, storage extensions, and shared infrastructure that ALL user stories depend on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Stack A — Types & Auth

- [ ] T004 Add `ScoringPrompt` interface and `PromptStatus` (`draft`|`active`|`inactive`|`production-approved`), `PromptSource` (`manual`|`imported`|`generated`) type unions to `src/types/index.ts` with all 12 fields from data-model.md (promptId, jobId, versionNumber, promptText, status, createdAt, lastModifiedAt, author, rating, comments, source, generationMetadata)
- [ ] T005 [P] Add `PromptTestRun` interface and `TestRunStatus` (`pending_scoring`|`scoring`|`pending_review`|`approved`|`rejected`) type union to `src/types/index.ts` with all 9 fields from data-model.md (testRunId, jobId, promptId, status, applicationIds, createdAt, completedAt, reviewedBy, reviewNotes)
- [ ] T006 [P] Add optional `testRunId?: string` field to the existing `Application` interface in `src/types/index.ts` per R5
- [ ] T007 [P] Implement AWR auth helper `getAwrAuthHeaders(user)` in `server/services/awr-auth.ts` supporting three modes: `none` (no headers), `apikey` (X-Api-Key from `AWR_API_KEY`, X-User-Id, X-User-Role), `entra` (Bearer JWT via `@azure/identity` DefaultAzureCredential) per FR-049/R6
- [ ] T008 [P] Add startup validation for required AWR env vars in `server/index.ts` — fail with descriptive error if `AWR_API_KEY` missing when `AWR_AUTH_MODE=apikey` or `AWR_AAD_AUDIENCE` missing when `AWR_AUTH_MODE=entra` per FR-050
- [ ] T009 [P] Add KV storage key patterns for prompts and test runs in `server/storage/kv-keys.ts` — keys: `job:{jobId}:prompt:{promptId}`, `job:{jobId}:prompt-list`, `job:{jobId}:prompt:{promptId}:test-run:{testRunId}`, `job:{jobId}:prompt:{promptId}:test-runs`

### Stack B — Domain, Infrastructure, Auth

- [ ] T010 [P] Implement `ScoringPrompt` entity in `dotnet/src/Domain/Entities/ScoringPrompt.cs` with all 12 fields from data-model.md
- [ ] T011 [P] Implement `PromptStatus` enum in `dotnet/src/Domain/Enums/PromptStatus.cs` (Draft, Active, Inactive, ProductionApproved)
- [ ] T012 [P] Implement `PromptSource` enum in `dotnet/src/Domain/Enums/PromptSource.cs` (Manual, Imported, Generated)
- [ ] T013 [P] Implement `PromptTestRun` entity in `dotnet/src/Domain/Entities/PromptTestRun.cs` with all 9 fields from data-model.md
- [ ] T014 [P] Implement `TestRunStatus` enum in `dotnet/src/Domain/Enums/TestRunStatus.cs` (PendingScoring, Scoring, PendingReview, Approved, Rejected) per R13
- [ ] T015 [P] Add optional `TestRunId` property to existing `Application` entity in `dotnet/src/Domain/Entities/Application.cs`
- [ ] T016 [P] Implement `IScoringPromptRepository` interface in `dotnet/src/Domain/Interfaces/IScoringPromptRepository.cs` with CRUD + GetByJobId + GetActiveForJob + GetProductionApprovedForJob methods
- [ ] T017 [P] Implement `IPromptTestRunRepository` interface in `dotnet/src/Domain/Interfaces/IPromptTestRunRepository.cs` with CRUD + GetByPromptId methods
- [ ] T018 Implement `ScoringPromptRepository` in `dotnet/src/Infrastructure/Persistence/Repositories/ScoringPromptRepository.cs` using EF Core
- [ ] T019 [P] Implement `PromptTestRunRepository` in `dotnet/src/Infrastructure/Persistence/Repositories/PromptTestRunRepository.cs` using EF Core
- [ ] T020 Add EF Core migration `AddScoringPromptsAndTestRuns` in `dotnet/src/Infrastructure/Persistence/` — create `ScoringPrompts` table, `PromptTestRuns` table, add `TestRunId` column to `Applications`, add indexes per api-stack-b.md migration plan
- [ ] T021 [P] Configure `ScoringPrompt` and `PromptTestRun` entity mappings in `dotnet/src/Infrastructure/Persistence/TalentMatchDbContext.cs`
- [ ] T022 [P] Implement AWR auth `DelegatingHandler` in `dotnet/src/Infrastructure/Services/AwrAuthHandler.cs` — read `AWR_AUTH_MODE` and attach headers per FR-049/R6; register via `IHttpClientFactory` in DI
- [ ] T023 [P] Add startup validation for required AWR env vars in `dotnet/src/Web.Server/Program.cs` — same logic as T008

### Shared — Document Content Infrastructure

- [ ] T024 Implement document content storage in upload route — persist raw file content (base64) under `app:{applicationId}:doc:{documentId}:blob` KV key in `server/routes/applications.ts` per FR-056
- [ ] T025 [P] Implement `GET /api/applications/:applicationId/documents/:documentId/content` endpoint in `server/routes/applications.ts` returning raw file bytes with correct `Content-Type` and `Content-Disposition: inline` per FR-057
- [ ] T026 [P] Implement equivalent document content endpoint in `dotnet/src/Web.Server/Endpoints/ApplicationsEndpoints.cs` per FR-057

**Checkpoint**: Foundation ready — all types, entities, auth, and storage infrastructure in place. User story implementation can now begin.

---

## Phase 3: User Story 1 — Recruiter authenticates and reaches dashboard (Priority: P1) 🎯 MVP

**Goal**: Recruiters log in with username/password, land on a department-filtered dashboard. Admins see all jobs.

**Independent Test**: Login with admin credentials → verify all-department dashboard. Create a recruiter scoped to "Engineering" → login → verify only Engineering jobs appear.

### Stack A Implementation

- [ ] T027 [US1] Implement login form with username/password fields, error display for invalid credentials, and redirect to dashboard on success in `src/components/LoginForm.tsx` per US1 scenarios 1–3
- [ ] T028 [US1] Implement `POST /api/auth/login` route in `server/routes/auth.ts` — authenticate via SHA-256 hash comparison, set session, return User object (without passwordHash) per api-stack-a.md
- [ ] T029 [P] [US1] Implement `POST /api/auth/logout` route clearing session in `server/routes/auth.ts` per US1 scenario 4
- [ ] T030 [P] [US1] Implement `GET /api/auth/me` route returning current authenticated user in `server/routes/auth.ts`
- [ ] T031 [US1] Implement auth middleware in `server/middleware/auth.ts` — validate session on all `/api/` routes except login; return 401 if unauthenticated
- [ ] T032 [US1] Implement RBAC middleware in `server/middleware/rbac.ts` — enforce department-based job filtering for recruiters per FR-002
- [ ] T033 [US1] Implement `DashboardView` component in `src/components/DashboardView.tsx` — display job list filtered by role (admin sees all, recruiter sees own department only), job cards with title/department/org/posting date per US1 scenarios 5–6
- [ ] T034 [P] [US1] Implement API client auth functions (login, logout, getMe) in `src/lib/api.ts`, `src/lib/api-real.ts`, and `src/lib/api-mock.ts`

### Stack B Implementation

- [ ] T035 [P] [US1] Implement `POST /api/auth/login` with SHA-256 hash comparison and cookie auth (`HttpContext.SignInAsync`) in `dotnet/src/Web.Server/Endpoints/AuthEndpoints.cs` per FR-021 (HttpOnly, SameSite=Strict, SecurePolicy=SameAsRequest)
- [ ] T036 [P] [US1] Implement `POST /api/auth/logout` and `GET /api/auth/me` in `dotnet/src/Web.Server/Endpoints/AuthEndpoints.cs`
- [ ] T037 [US1] Implement `GetCurrentUserQuery` handler in `dotnet/src/Application/Auth/` using `ICurrentUserService`
- [ ] T038 [US1] Implement Blazor WASM Login page in `dotnet/src/Web.Client/Pages/Login.razor` with username/password form, error handling, and redirect per US1 scenarios 1–3
- [ ] T039 [US1] Implement Blazor WASM Dashboard page in `dotnet/src/Web.Client/Pages/Dashboard.razor` — display role-filtered job list per US1 scenarios 5–6
- [ ] T040 [US1] Configure `BrowserRequestCredentials.Include` via `DelegatingHandler` in Blazor WASM `HttpClient` setup per FR-021
- [ ] T041 [US1] Implement default admin user seeding on first launch in `dotnet/src/Web.Server/Program.cs` (username=`admin`, SHA-256 hash, role=admin, department=`all`) per FR-020
- [ ] T042 [US1] Ensure Stack B Web.Server hosts Blazor WASM — configure `UseBlazorFrameworkFiles()`, `MapFallbackToFile("index.html")`, and serve static assets per FR-019 / US1 scenario 7

**Checkpoint**: US1 complete — authentication works end-to-end for both stacks. Dashboard shows role-filtered jobs.

---

## Phase 4: User Story 2 — Admin manages users and passwords (Priority: P1)

**Goal**: Admin can create/delete users, reset passwords, approve/reject reset requests. All users can change their own password.

**Independent Test**: As admin, create a recruiter, reset their password, then approve a pending reset request from a different user.

### Stack A Implementation

- [ ] T043 [US2] Implement `GET /api/users` route (admin only, excludes passwordHash) in `server/routes/users.ts`
- [ ] T044 [P] [US2] Implement `POST /api/users` route with validation (unique username, recruiter requires department) in `server/routes/users.ts` per data-model.md User validation rules
- [ ] T045 [P] [US2] Implement `DELETE /api/users/:userId` route with self-deletion block in `server/routes/users.ts` per US2 scenario 6
- [ ] T046 [P] [US2] Implement `POST /api/users/:userId/reset-password` route (admin only) in `server/routes/users.ts`
- [ ] T047 [US2] Implement password reset request endpoints (`GET /api/users/reset-requests`, `POST /api/users/reset-requests`, `PUT /api/users/reset-requests/:requestId`) in `server/routes/users.ts` per US2 scenarios 3–4
- [ ] T048 [US2] Implement `POST /api/auth/change-password` route validating current password before update in `server/routes/auth.ts` per US2 scenario 5
- [ ] T049 [US2] Implement `UserManagementDialog` component in `src/components/UserManagementDialog.tsx` — user list table, create user form, delete/reset controls, pending reset requests tab with approve/reject per FR-029, US2 scenarios 1–4
- [ ] T050 [P] [US2] Implement `ChangePasswordDialog` component in `src/components/ChangePasswordDialog.tsx` — current password, new password, confirmation fields with API call and error feedback per FR-024, US2 scenario 5
- [ ] T051 [P] [US2] Implement `UserMenu` component in `src/components/UserMenu.tsx` — display username, role badge, department; controls for logout, change password, and admin-only user management access per FR-022

### Stack B Implementation

- [ ] T052 [P] [US2] Implement user CRUD endpoints in `dotnet/src/Web.Server/Endpoints/UsersEndpoints.cs` — GET /api/users, POST /api/users, DELETE /api/users/{userId}, POST /api/users/{userId}/reset-password (all admin only) per api-stack-b.md
- [ ] T053 [P] [US2] Implement `CreateUserCommand` handler in `dotnet/src/Application/Users/Commands/` with FluentValidation (unique username, recruiter requires department, SHA-256 hash) per FR-031
- [ ] T054 [P] [US2] Implement `DeleteUserCommand` handler blocking self-deletion per US2 scenario 6
- [ ] T055 [US2] Implement password reset request endpoints and handlers: `GetResetRequestsQuery`, `RequestPasswordResetCommand`, `ResolveResetRequestCommand` in `dotnet/src/Application/Users/` per api-stack-b.md
- [x] T055 [US2] Implement password reset request endpoints and handlers: `GetResetRequestsQuery`, `RequestPasswordResetCommand`, `ResolveResetRequestCommand` in `dotnet/src/Application/Users/` per api-stack-b.md
- [ ] T056 [US2] Implement `ChangePasswordCommand` handler in `dotnet/src/Application/Auth/` validating current password first
- [ ] T057 [US2] Implement Blazor WASM `UserManagement.razor` component in `dotnet/src/Web.Client/Components/` — user list, create/delete/reset controls, reset requests tab per FR-029
- [ ] T058 [P] [US2] Implement Blazor WASM `ChangePasswordDialog.razor` in `dotnet/src/Web.Client/Components/` per FR-024
- [ ] T059 [P] [US2] Implement Blazor WASM `UserMenu.razor` in `dotnet/src/Web.Client/Shared/` displaying username, role badge, department with logout/change-password/user-management controls per FR-022
- [ ] T060 [US2] Ensure all Stack B mutating operations check API response, show success/error feedback, and do NOT close dialogs on failure per FR-030

**Checkpoint**: US2 complete — admin user management and password lifecycle works end-to-end.

---

## Phase 5: User Story 3 — Recruiter creates a job with rubric and scoring config (Priority: P2)

**Goal**: Recruiter creates jobs with title/department/org/posting date, uploads job spec/rubric documents for AI extraction, configures weighted rubric categories, must-haves, scoring runs, aggregation strategy, and thresholds. Rubric must be explicitly approved before downstream use.

**Independent Test**: Create a job end-to-end with extracted rubric; job card appears on dashboard with correct metadata.

### Stack A Implementation

- [ ] T061 [US3] Implement `POST /api/jobs` route in `server/routes/jobs.ts` — validate rubric weights sum to 1.0, shortlistThreshold > longlistThreshold, recruiter department enforcement; persist Job + JobConfigVersion per data-model.md
- [ ] T062 [US3] Implement `GET /api/jobs` route in `server/routes/jobs.ts` — return jobs filtered by department for recruiters, all for admins, with embedded stats per api-stack-a.md
- [ ] T063 [P] [US3] Implement `GET /api/jobs/:jobId` route returning full job detail with computed stats in `server/routes/jobs.ts`
- [ ] T064 [P] [US3] Implement `PUT /api/jobs/:jobId/config` route creating a new JobConfigVersion without invalidating in-progress scoring per FR-006 in `server/routes/jobs.ts`
- [ ] T065 [US3] Implement `POST /api/jobs/extract-spec` route in `server/routes/jobs.ts` — send uploaded document to `AWR_SEQ_API_ENDPOINT/assess/passthrough` with extraction system prompt, return extracted metadata (title, description, requirements, rubric) per R3/US3 scenario 2, using AWR auth headers from `server/services/awr-auth.ts`
- [ ] T066 [P] [US3] Implement `POST /api/jobs/extract-rubric` route in `server/routes/jobs.ts` — extract rubric categories from uploaded rubric document via passthrough API per US3 scenario 3
- [ ] T067 [US3] Implement `CreateJobDialog` component in `src/components/CreateJobDialog.tsx` — job details form, job spec upload with AI extraction, rubric document upload, rubric category management (add/remove rows, weight validation summing to 1.0), must-have criteria list, desired criteria, scoring config (runs, aggregation, thresholds), rubric draft-to-approved toggle per FR-028, US3 scenarios 1–5
- [ ] T068 [P] [US3] Implement `UploadRubricDialog` component in `src/components/UploadRubricDialog.tsx` — file upload for rubric document, title mismatch warning per US3 scenario 3
- [ ] T069 [P] [US3] Implement `JobCard` component in `src/components/JobCard.tsx` — display organisation name, days since posting per US3 scenario 6
- [ ] T070 [US3] Implement `JobDetailView` component in `src/components/JobDetailView.tsx` — full job detail page with tabs for config, applications, lists, prompt management

### Stack B Implementation

- [ ] T071 [P] [US3] Implement `CreateJobCommand` handler in `dotnet/src/Application/Jobs/Commands/` with FluentValidation (rubric weights sum to 1.0, threshold validation, department enforcement)
- [ ] T072 [P] [US3] Implement `GetJobsQuery` and `GetJobDetailQuery` handlers in `dotnet/src/Application/Jobs/Queries/` with department filtering
- [ ] T073 [P] [US3] Implement `UpdateJobConfigCommand` handler creating new JobConfigVersion per FR-006
- [ ] T074 [US3] Implement `ExtractSpecCommand` and `ExtractRubricCommand` handlers in `dotnet/src/Application/Jobs/Commands/` — call passthrough API via `LlmProxyService` with AWR auth per R3
- [ ] T075 [US3] Implement job endpoints in `dotnet/src/Web.Server/Endpoints/JobsEndpoints.cs` — GET /api/jobs, POST /api/jobs, GET /api/jobs/{jobId}, PUT /api/jobs/{jobId}/config, POST /api/jobs/extract-spec, POST /api/jobs/extract-rubric per api-stack-b.md
- [ ] T076 [US3] Implement Blazor WASM `CreateJobDialog.razor` in `dotnet/src/Web.Client/Components/` — matching Stack A CreateJobDialog with rubric management, extraction, and draft-to-approved toggle per FR-028
- [ ] T077 [P] [US3] Implement Blazor WASM `JobDetail.razor` page in `dotnet/src/Web.Client/Pages/` — full job detail with tabs for config, applications, lists, prompt management
- [ ] T078 [P] [US3] Implement Blazor WASM `JobCard.razor` in `dotnet/src/Web.Client/Components/` displaying org name and days since posting

**Checkpoint**: US3 complete — jobs can be created with rubrics, documents extracted, and configs versioned.

---

## Phase 6: User Story 3a — Recruiter creates, tests, and approves scoring prompt (Priority: P1)

**Goal**: Prompt lifecycle management — create (manual/import/generate), version, activate, rate, test with real scoring, manual review of test results, approve for production. Production scoring is blocked until a prompt is approved.

**Independent Test**: Create a job with rubric. Generate a draft prompt via API. Edit, save, activate. Upload 3 test applications, verify scoring and test-case marking. Manual review each. Approve for production. Verify test cases excluded from production lists.

### Stack A — API Routes

- [ ] T079 [US3a] Implement `GET /api/jobs/:jobId/prompts` route in `server/routes/prompts.ts` — list all prompt revisions for a job, ordered by versionNumber per api-stack-a.md
- [ ] T080 [P] [US3a] Implement `POST /api/jobs/:jobId/prompts` route in `server/routes/prompts.ts` — create new prompt revision with auto-increment versionNumber, validate job has approved rubric per FR-033
- [ ] T081 [P] [US3a] Implement `GET /api/jobs/:jobId/prompts/:promptId` route in `server/routes/prompts.ts`
- [ ] T082 [US3a] Implement `PUT /api/jobs/:jobId/prompts/:promptId` route in `server/routes/prompts.ts` — editing creates a NEW revision with incremented version (original unchanged) per FR-035
- [ ] T083 [US3a] Implement `POST /api/jobs/:jobId/prompts/:promptId/activate` route in `server/routes/prompts.ts` — set to active, deactivate previous active prompt in single transaction per FR-036
- [ ] T084 [P] [US3a] Implement `POST /api/jobs/:jobId/prompts/:promptId/rate` route in `server/routes/prompts.ts` — accept rating 0–5 integer and optional comments per FR-037
- [ ] T085 [US3a] Implement `POST /api/jobs/:jobId/prompts/generate` route in `server/routes/prompts.ts` — send job's approved rubric to `AWR_SEQ_API_ENDPOINT/assess/passthrough` with prompt-generation system instruction, return draft promptText and generationMetadata per R3/FR-034
- [ ] T086 [US3a] Implement `POST /api/jobs/:jobId/prompts/:promptId/approve-production` route in `server/routes/prompts.ts` — precondition: at least one PromptTestRun for this prompt has status `approved` per FR-040; transition prompt to `production-approved`
- [ ] T087 [US3a] Implement prompt audit trail — log all prompt lifecycle events (creation, editing, activation, rating, commenting, testing, approval) to ProcessingEvent via `server/services/audit.ts` per FR-041

### Stack A — Test Run Routes

- [ ] T088 [US3a] Implement `POST /api/jobs/:jobId/prompts/:promptId/test-runs` route in `server/routes/prompts.ts` — accept file uploads, create test applications with `testRunId` set, set test run status to `pending_scoring`, auto-trigger scoring pipeline in background per FR-038/FR-048/R12
- [ ] T089 [US3a] Implement auto-trigger: after test application creation, fire `Promise.allSettled()` of `processApplication()` calls with `promptVersionId` override (bypassing production-approved gate per R11) in the background; update test run status from `pending_scoring` → `scoring` → `pending_review` per R12
- [ ] T090 [P] [US3a] Implement `GET /api/jobs/:jobId/prompts/:promptId/test-runs` and `GET .../test-runs/:testRunId` routes in `server/routes/prompts.ts` — include application details with scores per api-stack-a.md
- [ ] T091 [US3a] Implement `POST .../test-runs/:testRunId/approve` route in `server/routes/prompts.ts` — precondition: all test applications completed manual review without score changes per FR-039; mark test run as `approved`
- [ ] T092 [US3a] Add optional `promptVersionId` parameter to `processApplication()` in `server/services/pipeline.ts` — when provided, skip `getProductionApprovedPromptId()` gate and use override directly per R11

### Stack A — UI Components

- [ ] T093 [US3a] Implement `PromptManagement` component in `src/components/PromptManagement.tsx` — embedded in JobDetailView; prompt creation controls (manual editor, file import, generate button), version dropdown listing all revisions with version/date/status/rating, activate/edit/rate actions per US3a scenarios 1–10, 17
- [ ] T094 [US3a] Implement test run cards in `src/components/PromptManagement.tsx` — display test run status (pending_scoring/scoring/pending_review/approved/rejected), "Review Results" button when status is `pending_review` per FR-052
- [ ] T095 [US3a] Implement results summary dialog in `src/components/PromptManagement.tsx` — expandable dialog showing each test application's overall score, eligibility gate pass/fail, per-category sub-scores; "Open Manual Review" button launching US7 interface pre-filtered to test run applications per FR-052/US3a scenario 13
- [ ] T096 [US3a] Implement "Approve for Production" button logic — appears on test run card ONLY after all test apps have completed manual review with no score changes; NOT alongside "Review Results" per FR-054/US3a scenario 14
- [ ] T097 [US3a] Implement score comparison logic — auto-compare AI-assigned vs manual review scores per test application; if any category score adjusted, prompt user to mark test run as `rejected` or `acceptable`; rejected blocks production approval per FR-053/US3a scenario 15
- [ ] T098 [US3a] Implement production scoring gate UI — when no production-approved prompt exists for job, block scoring trigger and display message per US3a scenario 16
- [ ] T099 [P] [US3a] Implement prompt-related API client functions in `src/lib/api.ts`, `src/lib/api-real.ts`, `src/lib/api-mock.ts` — getPrompts, createPrompt, getPrompt, editPrompt, activatePrompt, ratePrompt, generatePrompt, approveForProduction, createTestRun, getTestRuns, getTestRun, approveTestRun

### Stack B — CQRS Handlers

- [ ] T100 [P] [US3a] Implement `CreatePromptCommand` handler in `dotnet/src/Application/Prompts/Commands/CreatePromptCommand.cs` — auto-increment versionNumber per job, validate job has approved rubric per FR-033
- [ ] T101 [P] [US3a] Implement `EditPromptCommand` handler in `dotnet/src/Application/Prompts/Commands/EditPromptCommand.cs` — create new revision with incremented version per FR-035
- [ ] T102 [P] [US3a] Implement `ActivatePromptCommand` handler in `dotnet/src/Application/Prompts/Commands/ActivatePromptCommand.cs` — deactivate previous active prompt per FR-036
- [ ] T103 [P] [US3a] Implement `RatePromptCommand` handler in `dotnet/src/Application/Prompts/Commands/RatePromptCommand.cs` — validate rating 0–5 per FR-037
- [ ] T104 [US3a] Implement `GeneratePromptCommand` handler in `dotnet/src/Application/Prompts/Commands/GeneratePromptCommand.cs` — call passthrough API via `LlmProxyService` with rubric context per R3/FR-034
- [ ] T105 [US3a] Implement `ApprovePromptForProductionCommand` handler in `dotnet/src/Application/Prompts/Commands/ApprovePromptForProductionCommand.cs` — validate test run with `approved` status exists per FR-040
- [ ] T106 [P] [US3a] Implement `GetPromptsQuery` and `GetPromptQuery` handlers in `dotnet/src/Application/Prompts/Queries/`

### Stack B — Test Run Handlers

- [ ] T107 [US3a] Implement `CreatePromptTestRunCommand` handler in `dotnet/src/Application/Prompts/Commands/CreatePromptTestRunCommand.cs` — create test applications with `TestRunId`, set status `PendingScoring`, auto-trigger scoring via `Task.Run()` dispatching `ScoreApplicationCommand` with test prompt ID per R11/R12/FR-048
- [ ] T108 [US3a] Implement `ApprovePromptTestRunCommand` handler in `dotnet/src/Application/Prompts/Commands/ApprovePromptTestRunCommand.cs` — validate all test applications reviewed without score changes per FR-039
- [ ] T109 [P] [US3a] Implement `GetPromptTestRunsQuery` and `GetPromptTestRunQuery` handlers in `dotnet/src/Application/Prompts/Queries/`

### Stack B — Endpoints & UI

- [ ] T110 [US3a] Implement all prompt endpoints in `dotnet/src/Web.Server/Endpoints/PromptEndpoints.cs` — 8 endpoints for CRUD, activate, rate, generate, approve-production per api-stack-b.md
- [ ] T111 [P] [US3a] Implement test run endpoints in `dotnet/src/Web.Server/Endpoints/PromptEndpoints.cs` (or separate `PromptTestRunEndpoints.cs`) — 4 endpoints for create, list, get, approve per api-stack-b.md
- [ ] T112 [US3a] Implement Blazor WASM `PromptManagement.razor` in `dotnet/src/Web.Client/Components/` — prompt list/create/edit/generate/activate/rate per FR-055
- [ ] T113 [US3a] Implement Blazor WASM `PromptEditor.razor` in `dotnet/src/Web.Client/Components/` — rich text editor for prompt content
- [ ] T114 [US3a] Implement Blazor WASM `PromptTestRunner.razor` in `dotnet/src/Web.Client/Components/` — upload test apps, view test run cards with status, "Review Results" dialog, "Approve for Production" button per FR-052/FR-054/FR-055
- [ ] T115 [P] [US3a] Implement Blazor WASM `PromptRevisionList.razor` in `dotnet/src/Web.Client/Components/` — dropdown/list of all revisions with version, date, status, rating
- [ ] T116 [US3a] Add prompt and test-run API methods to Blazor WASM `ApiClient.cs` in `dotnet/src/Web.Client/Services/`

**Checkpoint**: US3a complete — full prompt lifecycle (create → test → approve) works. Production scoring gate enforced.

---

## Phase 7: User Story 4 — Recruiter uploads applications in bulk (Priority: P2)

**Goal**: Recruiter uploads document files (PDF/DOCX/MD/TXT/JPG) for candidates. Each uploaded application is validated, fingerprinted, stored with raw content, and queued for processing.

**Independent Test**: Upload 5 application files; verify all appear with status "Queued", correct fingerprints, file types, and sizes.

### Stack A Implementation

- [ ] T117 [US4] Implement `POST /api/jobs/:jobId/applications/upload` route in `server/routes/applications.ts` — validate file type (pdf/docx/md/txt/jpg) and size (≤50MB), compute SHA-256 fingerprint, detect duplicates per FR-007/FR-008, persist raw content (base64) per FR-056, create Application records with status `Queued`
- [ ] T118 [US4] Implement `UploadApplicationsDialog` component in `src/components/UploadApplicationsDialog.tsx` — drag-and-drop or file select, per-file progress indicator, inline validation errors for invalid types/sizes, duplicate warnings per US4 scenarios 1–4
- [ ] T119 [P] [US4] Implement upload-related API client functions in `src/lib/api.ts`, `src/lib/api-real.ts`, `src/lib/api-mock.ts` — uploadApplications

### Stack B Implementation

- [ ] T120 [P] [US4] Implement `UploadApplicationsCommand` handler in `dotnet/src/Application/Applications/Commands/` — same validation, fingerprinting, duplicate detection, and raw content persistence as Stack A
- [ ] T121 [US4] Implement upload endpoint in `dotnet/src/Web.Server/Endpoints/ApplicationsEndpoints.cs` — POST /api/jobs/{jobId}/applications/upload
- [ ] T122 [US4] Implement Blazor WASM `UploadApplications.razor` in `dotnet/src/Web.Client/Components/` — file upload dialog with drag-and-drop, progress, validation per FR-023

**Checkpoint**: US4 complete — applications can be uploaded, validated, fingerprinted, and queued for processing.

---

## Phase 8: User Story 5 — AI scoring pipeline runs and results are visible (Priority: P2)

**Goal**: Queued applications are scored via the external API with N configurable runs. Engine performs multi-run scoring and aggregation in a single response. Results stored directly. Supports sequential and platform scoring modes.

**Independent Test**: Trigger scoring for one application; verify N scoring run records + aggregated result with final decision. Test both sequential and platform modes.

### Stack A Implementation

- [ ] T123 [US5] Implement scoring mode detection in `server/services/pipeline.ts` — compare `AWR_PLATFORM_API_ENDPOINT` with `AWR_SEQ_API_ENDPOINT` at startup; log resolved mode (sequential or platform) per FR-061/FR-066
- [ ] T124 [US5] Implement `POST /api/jobs/:jobId/process` route in `server/routes/jobs.ts` — trigger pipeline for queued applications, enforce production-approved prompt gate (unless `promptVersionId` override per R11), dispatch to correct mode per FR-032/FR-066
- [ ] T125 [US5] Implement sequential scoring in `server/workers/scoring.ts` — send single request to `AWR_SEQ_API_ENDPOINT/assess/passthrough` with multipart FormData: `promptFile` (resolved prompt with `{{JOB_SPEC_TEXT}}` placeholder filled), `specFile` (original document blob with correct MIME type), `runs` parameter (N) per FR-045/FR-047/FR-062; parse combined response containing individual runs + aggregated result
- [ ] T126 [US5] Implement schema-agnostic JSON response parser in `server/workers/scoring.ts` — walk JSON generically by value type: numbers as scores, strings as evidence/recommendations, arrays of objects as category breakdowns; heuristic property name detection for total score, recommendations, notes per scoring-passthrough.md parsing rules / FR-045/FR-064
- [ ] T127 [US5] Implement platform mode scoring in `server/services/pipeline.ts` — submit to `AWR_PLATFORM_API_ENDPOINT/assess/batch` with multipart FormData (promptFile, specFile, runs, optional callbackUrl); poll status endpoint with exponential backoff (5s→10s→20s→30s cap); 15min max per scoring-platform.md / FR-063
- [ ] T128 [US5] Implement aggregated result storage — store engine-provided AggregatedResult directly (finalScore, variance, confidence, finalDecision, consolidated rationale, sub-score averages) in `server/workers/scoring.ts` without local re-aggregation per FR-011/FR-059
- [ ] T129 [US5] Implement failure handling — retry with exponential backoff on 5xx/network errors, move to DLQ after max retries in `server/workers/scoring.ts` per FR-013; update Application status (Queued → Scoring → Completed/NeedsManualReview/ScoringFailed)
- [ ] T130 [US5] Implement variance-based flagging — if aggregated variance > configured threshold, set Application.flagged=true and finalDecision=`NeedsManualReview` in `server/workers/scoring.ts` per FR-012
- [ ] T131 [P] [US5] Ensure non-scoring operations (prompt generation, extraction, test scoring) always use `AWR_SEQ_API_ENDPOINT` regardless of mode per FR-065

### Stack B Implementation

- [ ] T132 [P] [US5] Implement scoring mode detection in Stack B pipeline orchestrator — same logic as T123 per FR-066
- [ ] T133 [US5] Implement `ProcessJobCommand` handler in `dotnet/src/Application/Jobs/Commands/` — trigger scoring with production-approved prompt gate, dispatch to correct mode
- [ ] T134 [US5] Implement `ScoreApplicationCommand` handler in `dotnet/src/Application/Scoring/Commands/` — resolve prompt placeholders, call `LlmProxyService` with multipart FormData, parse combined response, store ScoringRun and AggregatedResult per FR-045/FR-047
- [ ] T135 [US5] Implement platform mode submission and polling in Stack B `LlmProxyService` — submit to `/assess/batch`, poll with backoff per scoring-platform.md
- [ ] T136 [US5] Implement schema-agnostic response parser in Stack B matching parsing rules from scoring-passthrough.md per FR-064
- [ ] T137 [US5] Implement scoring endpoint in `dotnet/src/Web.Server/Endpoints/JobsEndpoints.cs` — POST /api/jobs/{jobId}/process
- [ ] T138 [P] [US5] Ensure test scoring in Stack B always uses sequential endpoint per FR-065

**Checkpoint**: US5 complete — scoring pipeline runs in both sequential and platform modes, results stored with proper aggregation.

---

## Phase 9: User Story 6 — Recruiter views ranked lists and drills into application detail (Priority: P2)

**Goal**: Job detail shows longlist/shortlist/exclusions tabs. Clicking a candidate opens drill-down with documents, scoring runs, evidence, and aggregated decision.

**Independent Test**: Navigate to completed job; verify lists correctly categorised; drill into one application and verify all run details shown.

### Stack A Implementation

- [ ] T139 [US6] Implement `GET /api/jobs/:jobId/applications` route in `server/routes/applications.ts` — filter by status/list (longlist|shortlist|exclusions), sort by score/name, pagination, variance filter; EXCLUDE applications with `testRunId != null` from production lists per FR-016/FR-038
- [ ] T140 [US6] Implement `GET /api/applications/:applicationId` route in `server/routes/applications.ts` — return full application detail with documents, scoring runs, aggregated result per api-stack-a.md
- [ ] T141 [P] [US6] Implement `GET /api/applications/:applicationId/runs` and `GET .../result` routes in `server/routes/applications.ts`
- [ ] T142 [US6] Implement `ApplicationsTable` component in `src/components/ApplicationsTable.tsx` — longlist/shortlist/exclusions tabs with correct counts, sortable columns (score, name), variance filter per US6 scenarios 1–3
- [ ] T143 [US6] Implement `ApplicationDetail` component in `src/components/ApplicationDetail.tsx` — display original documents rendered natively (PDF via iframe, DOCX via mammoth.js, MD via renderer, TXT as pre, images inline), all N scoring runs with per-category breakdown and evidence, aggregated final decision with rationale and improvement tips per FR-025/FR-058/US6 scenarios 4–6
- [ ] T144 [US6] Implement `DocumentViewer` component in `src/components/DocumentViewer.tsx` — native format rendering for PDF/DOCX/MD/TXT/JPG with tabbed switching for multiple documents per FR-058
- [ ] T145 [P] [US6] Implement application-related API client functions in `src/lib/api.ts`, `src/lib/api-real.ts`, `src/lib/api-mock.ts` — getApplications, getApplication, getScoringRuns, getAggregatedResult, getDocumentContent

### Stack B Implementation

- [ ] T146 [P] [US6] Implement `GetApplicationsQuery` handler in `dotnet/src/Application/Applications/Queries/` — filter/sort/paginate, exclude test applications per FR-038
- [ ] T147 [P] [US6] Implement `GetApplicationDetailQuery`, `GetScoringRunsQuery`, `GetAggregatedResultQuery` handlers in `dotnet/src/Application/Applications/Queries/`
- [ ] T148 [US6] Implement application list and detail endpoints in `dotnet/src/Web.Server/Endpoints/ApplicationsEndpoints.cs` per api-stack-b.md
- [ ] T149 [US6] Implement Blazor WASM `ApplicationList.razor` in `dotnet/src/Web.Client/Components/` — longlist/shortlist/exclusions tabs with sorting and filtering
- [ ] T150 [US6] Implement Blazor WASM `ApplicationDetail.razor` page in `dotnet/src/Web.Client/Pages/` — native document rendering, scoring runs, aggregated result per FR-025
- [ ] T151 [P] [US6] Implement Blazor WASM `DocumentViewer.razor` in `dotnet/src/Web.Client/Components/` — native rendering with tabbed document switching per FR-058

**Checkpoint**: US6 complete — ranked lists display correctly, application drill-down shows full detail with native document rendering.

---

## Phase 10: User Story 7 — Recruiter performs manual review (Priority: P3)

**Goal**: Three-pane manual review interface — original document (left), rubric scoring form (centre), job spec + rubric (right). Per-category point allocation, live score updates, immutable audit trail.

**Independent Test**: Open manual review for a flagged application; allocate points in all categories; save; reload and verify persistence with audit trail entries.

### Stack A Implementation

- [ ] T152 [US7] Implement `GET /api/applications/:applicationId/manual-review` route in `server/routes/applications.ts` — return existing ManualReviewData or null
- [ ] T153 [US7] Implement `POST /api/applications/:applicationId/manual-review` route in `server/routes/applications.ts` — save rubric scores, overall comment, optional adjusted final score; append immutable audit entry recording reviewer, timestamp, category, previous/new value per FR-014
- [ ] T154 [US7] Implement `ManualReviewView` component in `src/components/ManualReviewView.tsx` — three resizable panes: left (native document rendering via DocumentViewer), centre (rubric scoring form with per-category point inputs and live weighted score recalculation), right (job specification and rubric categories/must-haves); pre-populate with existing scores on reload per US7 scenarios 1–5
- [ ] T155 [US7] Implement audit trail display in `src/components/ManualReviewView.tsx` — chronological list of all changes showing reviewer name, timestamp, category, old/new value per FR-014
- [ ] T156 [US7] Support test-run filtering — when opened from prompt management test results dialog, pre-filter to show only test applications from the specific test run per FR-052/US3a scenario 13

### Stack B Implementation

- [ ] T157 [P] [US7] Implement `GetManualReviewQuery` handler in `dotnet/src/Application/Applications/Queries/`
- [ ] T158 [P] [US7] Implement `SaveManualReviewCommand` handler in `dotnet/src/Application/Applications/Commands/` — persist rubric scores, comment, append audit entry per FR-014
- [ ] T159 [US7] Implement manual review endpoints in `dotnet/src/Web.Server/Endpoints/ApplicationsEndpoints.cs` — GET and POST for manual-review per api-stack-b.md
- [ ] T160 [US7] Implement Blazor WASM `ManualReview.razor` page in `dotnet/src/Web.Client/Pages/` — three-pane layout with native document rendering, rubric scoring form with live recalculation, job spec display, audit trail per FR-026/FR-058
- [ ] T161 [US7] Support test-run filtering in Blazor WASM ManualReview — pre-filter for test applications per FR-052

**Checkpoint**: US7 complete — manual review interface works with audit trail for both production and test-run applications.

---

## Phase 11: User Story 8 — Admin/recruiter monitors the processing pipeline (Priority: P3)

**Goal**: Dashboard shows system-wide stats, pipeline visualiser per job, failure queue with retry controls. Stats auto-refresh ≤30s.

**Independent Test**: While jobs process, verify stats update without refresh; simulate failure and verify it appears in DLQ with retry option.

### Stack A Implementation

- [ ] T162 [US8] Implement `GET /api/stats` route in `server/routes/stats.ts` — compute SystemStats excluding test applications (testRunId != null) from all counts per FR-038/data-model.md SystemStats
- [ ] T163 [P] [US8] Implement `GET /api/dlq` route in `server/routes/dlq.ts` — return FailureQueueItem list (admin only)
- [ ] T164 [P] [US8] Implement `POST /api/dlq/:itemId/retry` route in `server/routes/dlq.ts` — move item back to processing queue per US8 scenario 3
- [ ] T165 [P] [US8] Implement `GET /api/audit` route in `server/routes/audit.ts` — paginated ProcessingEvent list with entityType/eventType/date filters per api-stack-a.md
- [ ] T166 [US8] Add 30-second auto-refresh polling to `DashboardView` in `src/components/DashboardView.tsx` for stats and job list per FR-015/SC-003
- [ ] T167 [US8] Implement `StatCard` component in `src/components/StatCard.tsx` — display queued/processing/completed/failed counts
- [ ] T168 [US8] Implement `PipelineVisualizer` component in `src/components/PipelineVisualizer.tsx` — per-job pipeline stages (scoring → complete) with counts and percentage per US8 scenario 4
- [ ] T169 [US8] Implement `FailureQueueView` component in `src/components/FailureQueueView.tsx` — DLQ items with error details, failure reason, retry count, timestamps, retry button, 30s auto-refresh per FR-027/US8 scenario 3
- [ ] T170 [P] [US8] Implement department and organisation filtering on dashboard per US8 scenario 2

### Stack B Implementation

- [ ] T171 [P] [US8] Implement `GetStatsQuery` handler in `dotnet/src/Application/Stats/` excluding test applications per FR-038
- [ ] T172 [P] [US8] Implement `GetDlqItemsQuery` and `RetryDlqItemCommand` handlers in `dotnet/src/Application/Dlq/`
- [ ] T173 [P] [US8] Implement `GetAuditEventsQuery` handler in `dotnet/src/Application/Audit/`
- [ ] T174 [US8] Implement stats, DLQ, and audit endpoints in `dotnet/src/Web.Server/Endpoints/` — StatsEndpoints.cs, DlqEndpoints.cs, AuditEndpoints.cs per api-stack-b.md
- [ ] T175 [US8] Implement Blazor WASM Dashboard stats section with 30s auto-refresh, stat cards, department/org filtering in `dotnet/src/Web.Client/Pages/Dashboard.razor`
- [ ] T176 [US8] Implement Blazor WASM `PipelineVisualizer.razor` in `dotnet/src/Web.Client/Components/`
- [ ] T177 [US8] Implement Blazor WASM `FailureQueue.razor` in `dotnet/src/Web.Client/Pages/` — DLQ view with retry controls and 30s refresh per FR-027

**Checkpoint**: US8 complete — operational monitoring works with auto-refresh, failure retry, and audit visibility.

---

## Phase 12: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that span multiple user stories, performance, security hardening, and validation

- [ ] T178 [P] Implement error handling middleware with consistent error response format in `server/middleware/error-handler.ts` — catch unhandled errors, return structured JSON errors
- [ ] T179 [P] Implement request validation middleware in `server/middleware/validate.ts` — Zod schema validation for all POST/PUT request bodies
- [ ] T180 [P] Implement WCAG contrast ratio compliance (≥4.5:1) across all UI components per plan.md constraints
- [ ] T181 Verify all Stack B Blazor WASM navigation entry points are accessible — "Create Job" on Dashboard, "Upload Applications" on Job Detail, User Management from UserMenu (admin-only), Change Password from UserMenu, Failure Queue from navigation per FR-023
- [ ] T182 [P] Implement `SyncStatus` indicator component in `src/components/SyncStatus.tsx` — visible sync state for optimistic updates with rollback
- [ ] T183 Verify test application exclusion from ALL production-facing statistics — system-wide dashboard counts, per-recruiter analytics, per-department analytics, per-job application counts in both stacks per FR-038
- [ ] T184 [P] Implement `AnalyticsView` component in `src/components/AnalyticsView.tsx` — recruiter and department analytics views
- [ ] T185 Verify prompt lifecycle audit completeness — all events (creation, editing, activation, deactivation, rating, commenting, test-run creation, test-run approval/rejection, production approval) recorded in ProcessingEvent with actor, timestamp, correlationId per FR-041
- [ ] T186 Run `specs/001-talent-matching-platform/quickstart.md` validation — verify both stacks start successfully and all user stories are manually verifiable per quickstart guide
- [ ] T187 [P] Verify scoring mode logging — confirm both stacks log resolved scoring mode (sequential/platform) at startup per FR-061
- [ ] T188 [P] Ensure no client-side API keys — verify all LLM/AWR calls are proxied through backend per FR-017
- [ ] T189 Performance check — verify pagination for large tables (20K applications), progressive content rendering, ≤30s dashboard staleness per SC-001/SC-003
- [ ] T190 [P] Code cleanup — remove deprecated extraction worker invocations from pipeline flow (retain files for backward compatibility) per FR-059
- [ ] T191 Implement Azure SQL cold-start retry policy in Stack A and Stack B connection initialization paths — bounded exponential backoff, transient-error detection, and retry budget aligned to 30-90s wake-up window per FR-068
- [ ] T192 [P] Validate Azure SQL cold-start behavior in both stacks by simulating first-connection transient failures/timeouts; verify retries, structured logs (attempt number, elapsed time, error), and final failure only after retry budget exhaustion per FR-068

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion — BLOCKS all user stories
- **US1 Auth (Phase 3)**: Depends on Foundational (Phase 2) — BLOCKS US2 (admin features need auth)
- **US2 User Mgmt (Phase 4)**: Depends on US1 (auth required)
- **US3 Job Creation (Phase 5)**: Depends on Foundational (Phase 2) — can run in parallel with US1/US2
- **US3a Prompt Mgmt (Phase 6)**: Depends on US3 (job with rubric required) AND US1 (auth required)
- **US4 Upload (Phase 7)**: Depends on US3 (job must exist) AND US1 (auth required)
- **US5 Scoring Pipeline (Phase 8)**: Depends on US3a (production-approved prompt required) AND US4 (applications must exist)
- **US6 Ranked Lists (Phase 9)**: Depends on US5 (scored applications required)
- **US7 Manual Review (Phase 10)**: Depends on US6 (application detail needed) — also used by US3a for test review
- **US8 Pipeline Monitor (Phase 11)**: Depends on US5 (pipeline must be running)
- **Polish (Phase 12)**: Depends on all desired user stories being complete

### User Story Dependencies (Graph)

```
Phase 1 (Setup)
  └─→ Phase 2 (Foundational) ─── BLOCKS ALL ───┐
       │                                         │
       ├─→ Phase 3 (US1: Auth) ─────────────────┤
       │    └─→ Phase 4 (US2: User Mgmt)        │
       │                                         │
       ├─→ Phase 5 (US3: Job Creation) ──────────┤
       │    └─→ Phase 6 (US3a: Prompts) ─────────┤
       │                                         │
       │    Phase 7 (US4: Upload) ←── US3 ───────┤
       │         └──┐                             │
       │            ├─→ Phase 8 (US5: Scoring) ←─ US3a
       │            │        └─→ Phase 9 (US6: Lists)
       │            │             └─→ Phase 10 (US7: Review) ←─ US3a (test review)
       │            │
       │            └─→ Phase 11 (US8: Monitoring)
       │
       └─→ Phase 12 (Polish) ←── all phases
```

### Within Each User Story

- Models/entities before services
- Services before routes/endpoints
- Routes/endpoints before UI components
- Stack A and Stack B can proceed in parallel
- Core implementation before integration tasks

### Parallel Opportunities

**Within Phase 2 (Foundational)**:
- All Stack A type definitions (T004–T006) can run in parallel
- All Stack B entity/enum definitions (T010–T015) can run in parallel
- All Stack B repository interfaces (T016–T017) can run in parallel
- Stack A and Stack B auth helpers (T007, T022) can run in parallel

**Across User Stories**:
- US1 (Auth) and US3 (Job Creation) can start in parallel after Phase 2
- Stack A and Stack B implementations within each story can run in parallel
- US4 (Upload) and US6 (Ranked Lists) UI work can start before backend is complete

---

## Parallel Example: User Story 3a (Prompt Management)

```bash
# Launch all Stack B CQRS handlers in parallel:
Task: T100 "CreatePromptCommand handler"
Task: T101 "EditPromptCommand handler"
Task: T102 "ActivatePromptCommand handler"
Task: T103 "RatePromptCommand handler"
Task: T106 "GetPromptsQuery/GetPromptQuery handlers"
Task: T109 "GetPromptTestRunsQuery/GetPromptTestRunQuery handlers"

# Launch Stack A routes in parallel where independent:
Task: T079 "GET /api/jobs/:jobId/prompts"
Task: T081 "GET /api/jobs/:jobId/prompts/:promptId"
Task: T084 "POST /api/jobs/:jobId/prompts/:promptId/rate"
Task: T090 "GET test-runs endpoints"

# After routes complete, launch UI:
Task: T093 "PromptManagement component"
Task: T099 "API client functions" (parallel with T093)
```

---

## Implementation Strategy

### MVP First (US1 + US2 + US3 + US3a)

1. Complete Phase 1: Setup (T001–T003)
2. Complete Phase 2: Foundational (T004–T026) — CRITICAL GATE
3. Complete Phase 3: US1 Auth (T027–T042)
4. Complete Phase 4: US2 User Mgmt (T043–T060)
5. Complete Phase 5: US3 Job Creation (T061–T078)
6. Complete Phase 6: US3a Prompt Management (T079–T116)
7. **STOP and VALIDATE**: Auth → User mgmt → Job creation → Prompt lifecycle all work end-to-end
8. Deploy/demo if ready — recruiters can create jobs, configure rubrics, and approve prompts

### Incremental Delivery

1. Setup + Foundational → Foundation ready
2. Add US1 (Auth) + US2 (Users) → Test independently → **Login & user management demo**
3. Add US3 (Jobs) → Test independently → **Job creation demo**
4. Add US3a (Prompts) → Test independently → **Prompt test-and-approve demo**
5. Add US4 (Upload) + US5 (Scoring) → Test → **Full pipeline demo**
6. Add US6 (Lists) → Test → **Ranked list viewing demo**
7. Add US7 (Manual Review) → Test → **Review workflow demo**
8. Add US8 (Monitoring) → Test → **Full platform demo**
9. Polish → Final validation

### Parallel Team Strategy

With multiple developers:

1. Team completes Setup + Foundational together
2. Once Foundational is done:
   - Developer A: US1 (Auth) → US2 (User Mgmt)
   - Developer B: US3 (Jobs) — can start in parallel with US1
   - Developer C: Stack B foundational entity implementations
3. After US1 + US3 complete:
   - Developer A: US3a (Prompts)
   - Developer B: US4 (Upload)
   - Developer C: Stack B CQRS handlers for US3a
4. After US3a + US4 complete:
   - Developer A: US5 (Scoring Pipeline)
   - Developer B: US6 (Ranked Lists)
   - Developer C: US7 (Manual Review)
5. Final stretch: US8 (Monitoring) + Polish

---

## Notes

- [P] tasks = different files, no dependencies — safe to run in parallel
- [Story] label maps task to specific user story for traceability
- Each user story should be independently completable and testable at its checkpoint
- This is a **brownfield** project — many files already exist. Verify existing implementations match spec requirements before adding new code
- Both stacks (Stack A: React/Express, Stack B: .NET/Blazor) must maintain feature parity per FR-055
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- Avoid: vague tasks, same file conflicts, cross-story dependencies that break independence
- All `testRunId`-bearing applications must be excluded from production statistics everywhere per FR-038
