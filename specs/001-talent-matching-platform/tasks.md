# Tasks: Talent Matching Platform

**Input**: Design documents from `/specs/001-talent-matching-platform/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅, quickstart.md ✅

**Tests**: Not explicitly requested in the feature specification — test tasks are omitted.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story. Both Stack A (React/TypeScript/Express) and Stack B (.NET/Blazor/Clean Architecture) are covered per story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **Stack A**: `src/` (React frontend), `server/` (Express backend)
- **Stack B**: `dotnet/src/Domain/`, `dotnet/src/Application/`, `dotnet/src/Infrastructure/`, `dotnet/src/Web.Server/`, `dotnet/src/Web.Client/`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Environment configuration, shared dependencies, and AWR auth infrastructure needed by both stacks

- [X] T001 Add AWR auth environment variables (`AWR_AUTH_MODE`, `AWR_API_KEY`, `AWR_AAD_ISSUER`, `AWR_AAD_AUDIENCE`) to `.env.example` and document in `specs/001-talent-matching-platform/quickstart.md`
- [X] T002 [P] Create AWR auth helper for Stack A in `server/services/awr-auth.ts` — implement `getAwrAuthHeaders(user)` supporting `none`, `apikey` (X-Api-Key, X-User-Id, X-User-Role), and `entra` (Bearer JWT via `@azure/identity` DefaultAzureCredential) modes per FR-049/FR-050
- [X] T003 [P] Create AWR auth DelegatingHandler for Stack B in `dotnet/src/Infrastructure/Services/AwrAuthHandler.cs` — implement `DelegatingHandler` that reads `AWR_AUTH_MODE` and attaches headers per FR-049/FR-050; register via `IHttpClientFactory` in DI
- [X] T004 [P] Add startup validation for required AWR env vars in Stack A `server/index.ts` — fail with descriptive error if `AWR_API_KEY` missing when `AWR_AUTH_MODE=apikey` or `AWR_AAD_AUDIENCE` missing when `AWR_AUTH_MODE=entra` per FR-050
- [X] T005 [P] Add startup validation for required AWR env vars in Stack B `dotnet/src/Web.Server/Program.cs` — same validation logic as T004

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: New domain entities, enums, storage, and type definitions that ALL user stories depend on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [X] T006 Add `ScoringPrompt` interface and `PromptStatus`, `PromptSource` enums to Stack A types in `src/types/index.ts` — fields: promptId, jobId, versionNumber, promptText, status (draft|active|inactive|production-approved), createdAt, lastModifiedAt, author, rating, comments, source (manual|imported|generated), generationMetadata per data-model.md
- [X] T007 [P] Add `PromptTestRun` interface and `TestRunStatus` enum to Stack A types in `src/types/index.ts` — fields: testRunId, jobId, promptId, status (pending_scoring|scoring|pending_review|approved|rejected), applicationIds, createdAt, completedAt, reviewedBy, reviewNotes per data-model.md and R13
- [X] T008 [P] Add optional `testRunId` field to the existing `Application` interface in `src/types/index.ts` — nullable FK to PromptTestRun per R5
- [X] T009 [P] Create `ScoringPrompt` entity in `dotnet/src/Domain/Entities/ScoringPrompt.cs` with all fields from data-model.md (promptId, jobId, versionNumber, promptText, status, createdAt, lastModifiedAt, author, rating, comments, source, generationMetadata)
- [X] T010 [P] Create `PromptStatus` enum in `dotnet/src/Domain/Enums/PromptStatus.cs` (Draft, Active, Inactive, ProductionApproved)
- [X] T011 [P] Create `PromptSource` enum in `dotnet/src/Domain/Enums/PromptSource.cs` (Manual, Imported, Generated)
- [X] T012 [P] Create `PromptTestRun` entity in `dotnet/src/Domain/Entities/PromptTestRun.cs` with all fields from data-model.md
- [X] T013 [P] Create `TestRunStatus` enum in `dotnet/src/Domain/Enums/TestRunStatus.cs` (PendingScoring, Scoring, PendingReview, Approved, Rejected) per R13
- [X] T014 [P] Add optional `TestRunId` property to existing `Application` entity in `dotnet/src/Domain/Entities/Application.cs`
- [X] T015 [P] Create `IScoringPromptRepository` interface in `dotnet/src/Domain/Interfaces/IScoringPromptRepository.cs` — CRUD + GetByJobId, GetActiveByJobId, GetProductionApprovedByJobId
- [X] T016 [P] Create `IPromptTestRunRepository` interface in `dotnet/src/Domain/Interfaces/IPromptTestRunRepository.cs` — CRUD + GetByPromptId, GetByJobId
- [X] T017 Implement `ScoringPromptRepository` in `dotnet/src/Infrastructure/Persistence/Repositories/ScoringPromptRepository.cs` using EF Core
- [X] T018 [P] Implement `PromptTestRunRepository` in `dotnet/src/Infrastructure/Persistence/Repositories/PromptTestRunRepository.cs` using EF Core
- [X] T019 Add EF Core entity configurations for `ScoringPrompt` and `PromptTestRun` in `dotnet/src/Infrastructure/Persistence/` DbContext — include IX_ScoringPrompts_JobId_Status and IX_Applications_TestRunId indexes
- [X] T020 Create EF Core migration `AddScoringPromptsAndTestRuns` in `dotnet/src/Infrastructure/` — add ScoringPrompts table, PromptTestRuns table, Applications.TestRunId nullable FK column per api-stack-b.md migration plan
- [X] T021 [P] Add ScoringPrompt and PromptTestRun KV storage key patterns in Stack A `server/storage/` — define key patterns for prompt and test-run data per R1/R2 implementation approach
- [X] T022 Register new repositories in Stack B DI container in `dotnet/src/Web.Server/Program.cs` (IScoringPromptRepository, IPromptTestRunRepository)

**Checkpoint**: Foundation ready — all new entities defined in both stacks. User story implementation can now begin.

---

## Phase 3: User Story 1 — Recruiter authenticates and reaches dashboard (Priority: P1) 🎯 MVP

**Goal**: Recruiter logs in and sees department-filtered dashboard; admin sees all jobs

**Independent Test**: Login with admin credentials → verify all-department dashboard. Create recruiter scoped to "Engineering" → verify only Engineering jobs appear.

### Implementation for User Story 1

- [X] T023 [P] [US1] Implement login screen with username/password fields in Stack A `src/components/LoginForm.tsx` — redirect to dashboard on success, show error on invalid credentials per US1 scenarios 1–3
- [X] T024 [P] [US1] Implement login API route in Stack A `server/routes/auth.ts` — POST `/api/auth/login` with session cookie, POST `/api/auth/logout`, GET `/api/auth/me` per api-stack-a.md auth contract
- [X] T025 [P] [US1] Implement auth middleware in Stack A `server/middleware/auth.ts` — session validation, attach user to request context
- [X] T026 [P] [US1] Implement RBAC middleware in Stack A `server/middleware/rbac.ts` — role-based route protection (admin-only routes, department filtering)
- [X] T027 [US1] Implement dashboard view in Stack A `src/components/DashboardView.tsx` — show job list filtered by recruiter's department (or all for admin), job cards with title, department, organisation, days since posting per US1 scenarios 5–6
- [X] T028 [P] [US1] Implement GET `/api/jobs` route in Stack A `server/routes/jobs.ts` — return jobs filtered by authenticated user's department (recruiter) or all (admin)
- [X] T029 [P] [US1] Implement `AuthEndpoints.cs` in Stack B `dotnet/src/Web.Server/Endpoints/AuthEndpoints.cs` — POST login (SHA-256 hash, SignInAsync with cookie), POST logout (SignOutAsync), GET /me per api-stack-b.md
- [X] T030 [P] [US1] Implement cookie auth configuration in Stack B `dotnet/src/Web.Server/Program.cs` — HttpOnly, SameSite=Strict, SecurePolicy=SameAsRequest per FR-021
- [X] T031 [P] [US1] Implement `GetJobsQuery` handler in Stack B `dotnet/src/Application/Jobs/Queries/GetJobsQuery.cs` — department-filtered job listing
- [X] T032 [P] [US1] Implement `JobEndpoints.cs` GET `/api/jobs` in Stack B `dotnet/src/Web.Server/Endpoints/JobEndpoints.cs`
- [X] T033 [US1] Implement Blazor login page in Stack B `dotnet/src/Web.Client/Pages/Login.razor` — username/password form, error handling, redirect to dashboard
- [X] T034 [US1] Implement Blazor dashboard page in Stack B `dotnet/src/Web.Client/Pages/Dashboard.razor` — job cards with department filtering per role
- [X] T035 [US1] Configure Blazor WASM hosting in Stack B `dotnet/src/Web.Server/Program.cs` — serve WebAssembly framework files, static assets, fallback to index.html per FR-019
- [X] T036 [US1] Implement `BrowserRequestCredentials.Include` DelegatingHandler in Stack B `dotnet/src/Web.Client/` — ensure cookies sent with every API request per FR-021
- [X] T037 [US1] Seed default admin user on first launch in Stack B `dotnet/src/Web.Server/Program.cs` or `Infrastructure/` — username `admin`, SHA-256 hashed password, role `admin`, department `all` per FR-020
- [X] T038 [US1] Implement Stack A default admin seeding in `server/services/init-users.ts` per FR-020

**Checkpoint**: Auth works end-to-end in both stacks. Recruiters see department-filtered jobs; admins see all.

---

## Phase 4: User Story 2 — Admin manages users and passwords (Priority: P1)

**Goal**: Admin creates/deletes users, resets passwords, approves reset requests; users change own password

**Independent Test**: As admin, create a recruiter, reset their password, then approve a pending reset request.

### Implementation for User Story 2

- [X] T039 [P] [US2] Implement user CRUD API routes in Stack A `server/routes/users.ts` — GET /api/users, POST /api/users, DELETE /api/users/:userId, POST /api/users/:userId/reset-password per api-stack-a.md
- [X] T040 [P] [US2] Implement password reset request routes in Stack A `server/routes/users.ts` — GET /api/users/reset-requests, POST /api/users/reset-requests, PUT /api/users/reset-requests/:requestId per api-stack-a.md
- [X] T041 [P] [US2] Implement change password route in Stack A `server/routes/auth.ts` — POST /api/auth/change-password with current password validation per US2 scenario 5
- [X] T042 [US2] Implement user management UI in Stack A `src/components/UserManagementDialog.tsx` — list users, create user form (username, role, department, password), delete button (block self-delete), reset password, pending reset requests tab per FR-029
- [X] T043 [P] [US2] Implement `UserEndpoints.cs` in Stack B `dotnet/src/Web.Server/Endpoints/UserEndpoints.cs` — all user CRUD and reset request endpoints per api-stack-b.md
- [X] T044 [P] [US2] Implement `CreateUserCommand` in Stack B `dotnet/src/Application/Users/Commands/CreateUserCommand.cs` — SHA-256 hash, recruiter must have department, unique username validation per FR-031
- [X] T045 [P] [US2] Implement `DeleteUserCommand` in Stack B `dotnet/src/Application/Users/Commands/DeleteUserCommand.cs` — block self-delete per US2 scenario 6
- [X] T046 [P] [US2] Implement `ResetPasswordCommand` in Stack B `dotnet/src/Application/Users/Commands/ResetPasswordCommand.cs`
- [X] T047 [P] [US2] Implement `ChangePasswordCommand` in Stack B `dotnet/src/Application/Users/Commands/ChangePasswordCommand.cs` — validate current password per US2 scenario 5
- [X] T048 [P] [US2] Implement reset request commands/queries in Stack B `dotnet/src/Application/Users/` — `RequestPasswordResetCommand`, `ResolveResetRequestCommand`, `GetResetRequestsQuery`
- [X] T049 [US2] Implement Blazor UserManagement component in Stack B `dotnet/src/Web.Client/Components/UserManagement.razor` — user list, create form, delete, reset password, pending requests per FR-029
- [X] T050 [US2] Implement Blazor UserMenu component in Stack B `dotnet/src/Web.Client/Components/UserMenu.razor` — display name, role badge, department, logout, change password, admin-only user management link per FR-022
- [X] T051 [US2] Implement Blazor ChangePassword dialog in Stack B `dotnet/src/Web.Client/Components/ChangePasswordDialog.razor` — current/new/confirm fields with API call and feedback per FR-024
- [X] T052 [US2] Implement Stack A UserMenu component in `src/components/UserMenu.tsx` — matching Stack B UserMenu functionality per FR-022

**Checkpoint**: Admin can manage all users in both stacks. Password flows work end-to-end.

---

## Phase 5: User Story 3 — Recruiter creates a job with rubric and scoring config (Priority: P2)

**Goal**: Recruiter creates a job with title, department, org, posting date, rubric categories (weights sum to 1.0), must-haves, desired criteria, scoring config, and optional document upload for AI extraction

**Independent Test**: Create a job end-to-end; job card appears on dashboard with correct metadata.

### Implementation for User Story 3

- [X] T053 [P] [US3] Implement create job API route in Stack A `server/routes/jobs.ts` — POST /api/jobs with rubric weight validation (sum to 1.0), must-haves, scoring config per api-stack-a.md
- [X] T054 [P] [US3] Implement job config update route in Stack A `server/routes/jobs.ts` — PUT /api/jobs/:jobId/config creating new version per FR-006
- [X] T055 [P] [US3] Implement GET /api/jobs/:jobId route in Stack A `server/routes/jobs.ts` — job detail with computed stats
- [X] T056 [P] [US3] Implement job spec extraction route in Stack A `server/routes/jobs.ts` — POST /api/jobs/extract-spec calling `AWR_SEQ_API_ENDPOINT/assess/passthrough` with AWR auth headers (T002) per US3 scenario 2
- [X] T057 [P] [US3] Implement rubric extraction route in Stack A `server/routes/jobs.ts` — POST /api/jobs/extract-rubric calling `AWR_SEQ_API_ENDPOINT/assess/passthrough` with AWR auth headers per US3 scenario 3
- [X] T058 [US3] Implement CreateJobDialog UI in Stack A `src/components/CreateJobDialog.tsx` — job form with rubric category management (add/remove, weight validation sum=1.0), must-have criteria list, desired criteria, scoring config (runsPerApplication, aggregationStrategy, thresholds), document upload for spec/rubric extraction, draft-to-approved rubric toggle per US3 scenarios 1–5 and FR-028
- [X] T059 [P] [US3] Implement job detail view in Stack A `src/components/JobDetailView.tsx` — display job metadata, config version, organisation, days since posting per US3 scenario 6
- [X] T060 [P] [US3] Implement `CreateJobCommand` in Stack B `dotnet/src/Application/Jobs/Commands/CreateJobCommand.cs` — validate rubric weights sum to 1.0, create JobConfigVersion per data-model.md
- [X] T061 [P] [US3] Implement `UpdateJobConfigCommand` in Stack B `dotnet/src/Application/Jobs/Commands/UpdateJobConfigCommand.cs` — create new config version per FR-006
- [X] T062 [P] [US3] Implement `GetJobDetailQuery` in Stack B `dotnet/src/Application/Jobs/Queries/GetJobDetailQuery.cs`
- [X] T063 [P] [US3] Implement `ExtractSpecCommand` and `ExtractRubricCommand` in Stack B `dotnet/src/Application/Jobs/Commands/` — call AWR passthrough via `LlmProxyService` with auth per FR-034
- [X] T064 [US3] Wire up all job endpoints in Stack B `dotnet/src/Web.Server/Endpoints/JobEndpoints.cs` — POST /api/jobs, GET /api/jobs/:jobId, PUT /api/jobs/:jobId/config, POST /api/jobs/extract-spec, POST /api/jobs/extract-rubric
- [X] T065 [US3] Implement Blazor CreateJobDialog in Stack B `dotnet/src/Web.Client/Components/CreateJobDialog.razor` — rubric management, must-haves, document upload with LLM extraction per FR-028
- [X] T066 [US3] Implement Blazor JobDetail page in Stack B `dotnet/src/Web.Client/Pages/JobDetail.razor` — job metadata, config display, navigation to prompts/applications

**Checkpoint**: Jobs can be created with full rubric configuration in both stacks. Document extraction works via AWR API.

---

## Phase 6: User Story 3a — Recruiter creates, tests, and approves a scoring prompt (Priority: P1)

**Goal**: Create/import/generate scoring prompts, version them, test with sample applications, manual review test cases, approve for production scoring

**Independent Test**: Create job with rubric → generate draft prompt → edit/save/activate → upload 3 test applications → verify scored as test cases → manual review each → approve for production → verify production scoring unblocked and test cases excluded from ranked lists.

**Dependencies**: Requires US3 (job with approved rubric must exist)

### Implementation for User Story 3a

#### Stack A — Backend (Express)

- [X] T067 [P] [US3a] Implement prompt CRUD routes in Stack A `server/routes/prompts.ts` — GET /api/jobs/:jobId/prompts (list), POST /api/jobs/:jobId/prompts (create with source), GET /api/jobs/:jobId/prompts/:promptId (detail) per api-stack-a.md
- [X] T068 [P] [US3a] Implement prompt edit route in Stack A `server/routes/prompts.ts` — PUT /api/jobs/:jobId/prompts/:promptId creating new revision with incremented versionNumber per FR-035
- [X] T069 [P] [US3a] Implement prompt activate route in Stack A `server/routes/prompts.ts` — POST /api/jobs/:jobId/prompts/:promptId/activate with single-active-per-job enforcement (deactivate previous) per FR-036, R4
- [X] T070 [P] [US3a] Implement prompt rate route in Stack A `server/routes/prompts.ts` — POST /api/jobs/:jobId/prompts/:promptId/rate with rating 0–5 and comments per FR-037
- [X] T071 [P] [US3a] Implement prompt generate route in Stack A `server/routes/prompts.ts` — POST /api/jobs/:jobId/prompts/generate calling `AWR_SEQ_API_ENDPOINT/assess/passthrough` with approved rubric context and AWR auth headers per FR-034, R3
- [X] T072 [US3a] Implement prompt production approval route in Stack A `server/routes/prompts.ts` — POST /api/jobs/:jobId/prompts/:promptId/approve-production with precondition check (PromptTestRun must be approved) per FR-040
- [X] T073 [US3a] Implement test-run creation route in Stack A `server/routes/prompts.ts` — POST /api/jobs/:jobId/prompts/:promptId/test-runs: create test applications with testRunId, set status pending_scoring, fire-and-forget scoring pipeline with promptVersionId override per FR-038, FR-048, R11, R12
- [X] T074 [P] [US3a] Implement test-run list and detail routes in Stack A `server/routes/prompts.ts` — GET /api/jobs/:jobId/prompts/:promptId/test-runs (list), GET .../test-runs/:testRunId (detail with application results)
- [X] T075 [US3a] Implement test-run approve route in Stack A `server/routes/prompts.ts` — POST .../test-runs/:testRunId/approve with precondition: all test applications reviewed, compare AI vs manual scores, if any score adjusted prompt user for accept/reject decision per FR-039, FR-053, FR-054
- [X] T076 [US3a] Modify scoring pipeline orchestrator in Stack A `server/services/pipeline.ts` — add optional `promptVersionId` parameter to `processApplication()`, skip production-approved gate when provided per R11
- [X] T077 [US3a] Update test-run status transitions in Stack A scoring pipeline — pending_scoring → scoring (first app starts) → pending_review (all apps scored and aggregated) per FR-048, R13
- [X] T078 [US3a] Exclude test-case applications from production ranked lists in Stack A `server/routes/applications.ts` — filter out applications with `testRunId != null` from GET /api/jobs/:jobId/applications per FR-038
- [X] T079 [US3a] Record all prompt lifecycle events in Stack A audit trail via `server/services/audit.ts` — prompt.created, prompt.activated, prompt.rated, prompt.tested, prompt.approved per FR-041

#### Stack A — Frontend (React)

- [X] T080 [US3a] Implement PromptManagement panel in Stack A `src/components/PromptManagement.tsx` — embedded in JobDetailView, prompt list dropdown (all revisions with version, date, status, rating), create/import/generate controls, activate button; enabled only when job has approved rubric per FR-033, US3a scenarios 1–2, 6
- [X] T081 [US3a] Implement PromptEditor component in Stack A `src/components/PromptEditor.tsx` — text editor for prompt content, save creates new revision, load existing for editing per US3a scenarios 4, 8
- [X] T082 [US3a] Implement PromptTestRunner component in Stack A `src/components/PromptTestRunner.tsx` — upload test applications, show test-run cards with status transitions (pending_scoring → scoring → pending_review), "Review Results" button per FR-052, US3a scenarios 11–12
- [X] T083 [US3a] Implement TestRunResultsDialog in Stack A `src/components/TestRunResultsDialog.tsx` — expandable results summary showing per-application overall score, eligibility gate pass/fail, per-category sub-scores, "Open Manual Review" button linking to US7 interface pre-filtered by testRunId per FR-052, US3a scenario 13
- [X] T084 [US3a] Implement "Approve for Production" button on test run card in Stack A `src/components/PromptTestRunner.tsx` — appears only after all test apps manually reviewed with no score changes; replaces "Review Results" button per FR-054, US3a scenario 14
- [X] T085 [US3a] Implement prompt rating and comments UI in Stack A `src/components/PromptManagement.tsx` — 0–5 star rating + text comments per prompt revision per FR-037, US3a scenario 10
- [X] T086 [US3a] Add prompt import/upload functionality in Stack A `src/components/PromptManagement.tsx` — file upload, load content into editor per US3a scenario 17

#### Stack B — Backend (.NET)

- [X] T087 [P] [US3a] Implement `CreatePromptCommand` in Stack B `dotnet/src/Application/Prompts/Commands/CreatePromptCommand.cs` — validate job has approved rubric, auto-increment versionNumber per job per FR-033, R4
- [X] T088 [P] [US3a] Implement `EditPromptCommand` in Stack B `dotnet/src/Application/Prompts/Commands/EditPromptCommand.cs` — create new revision with incremented versionNumber, preserve original per FR-035
- [X] T089 [P] [US3a] Implement `ActivatePromptCommand` in Stack B `dotnet/src/Application/Prompts/Commands/ActivatePromptCommand.cs` — deactivate current active, set selected to active in transaction per FR-036, R4
- [X] T090 [P] [US3a] Implement `RatePromptCommand` in Stack B `dotnet/src/Application/Prompts/Commands/RatePromptCommand.cs` — rating 0–5, comments per FR-037
- [X] T091 [P] [US3a] Implement `GeneratePromptCommand` in Stack B `dotnet/src/Application/Prompts/Commands/GeneratePromptCommand.cs` — call LlmProxyService with approved rubric context per FR-034, R3
- [X] T092 [US3a] Implement `ApprovePromptForProductionCommand` in Stack B `dotnet/src/Application/Prompts/Commands/ApprovePromptForProductionCommand.cs` — validate PromptTestRun approved per FR-040
- [X] T093 [P] [US3a] Implement `GetPromptsQuery` and `GetPromptQuery` in Stack B `dotnet/src/Application/Prompts/Queries/` — list by job, get by id
- [X] T094 [US3a] Implement `CreatePromptTestRunCommand` in Stack B `dotnet/src/Application/Prompts/Commands/CreatePromptTestRunCommand.cs` — create test applications with testRunId, set status pending_scoring, dispatch ScoreApplicationCommand with test prompt ID via Task.Run per FR-038, FR-048, R11, R12
- [X] T095 [P] [US3a] Implement `GetPromptTestRunsQuery` and `GetPromptTestRunQuery` in Stack B `dotnet/src/Application/Prompts/Queries/` — list by prompt, get by id with application details
- [X] T096 [US3a] Implement `ApprovePromptTestRunCommand` in Stack B `dotnet/src/Application/Prompts/Commands/ApprovePromptTestRunCommand.cs` — compare AI vs manual scores, prompt accept/reject decision per FR-039, FR-053, FR-054
- [X] T097 [US3a] Wire up prompt endpoints in Stack B `dotnet/src/Web.Server/Endpoints/PromptEndpoints.cs` — all 8 prompt endpoints per api-stack-b.md
- [X] T098 [US3a] Wire up test-run endpoints in Stack B `dotnet/src/Web.Server/Endpoints/PromptTestRunEndpoints.cs` — all 4 test-run endpoints per api-stack-b.md
- [X] T099 [US3a] Update `GetApplicationsQuery` in Stack B to exclude applications with `TestRunId != null` from production lists per FR-038
- [X] T100 [US3a] Record all prompt lifecycle events in Stack B audit trail — prompt.created, prompt.activated, prompt.rated, prompt.tested, prompt.approved per FR-041

#### Stack B — Frontend (Blazor)

- [X] T101 [US3a] Implement `PromptManagement.razor` in Stack B `dotnet/src/Web.Client/Components/PromptManagement.razor` — embedded in JobDetail, prompt list, create/import/generate controls per FR-033, FR-055
- [X] T102 [US3a] Implement `PromptEditor.razor` in Stack B `dotnet/src/Web.Client/Components/PromptEditor.razor` — text editor, save as new revision per FR-035, FR-055
- [X] T103 [US3a] Implement `PromptTestRunner.razor` in Stack B `dotnet/src/Web.Client/Components/PromptTestRunner.razor` — upload test apps, test-run cards with status, Review Results button per FR-052, FR-055
- [X] T104 [US3a] Implement `PromptRevisionList.razor` in Stack B `dotnet/src/Web.Client/Components/PromptRevisionList.razor` — dropdown/list of all revisions with version, date, status, rating per FR-055
- [X] T105 [US3a] Implement test run results summary dialog in Stack B Blazor — per-application scores, eligibility, sub-scores, "Open Manual Review" button per FR-052, FR-055
- [X] T106 [US3a] Add prompt API client methods to Stack B `dotnet/src/Web.Client/Services/ApiClient.cs` — all prompt and test-run endpoint calls

**Checkpoint**: Full prompt lifecycle works in both stacks — create/import/generate, version, activate, test, review, approve for production. Production scoring is blocked without approved prompt.

---

## Phase 7: User Story 4 — Recruiter uploads applications in bulk (Priority: P2)

**Goal**: Bulk upload candidate application documents (PDF, DOCX, MD, TXT, JPG) with validation, fingerprinting, and queueing

**Independent Test**: Upload 5 application files; verify all appear with status "Queued", correct fingerprints, types, and timestamps.

### Implementation for User Story 4

- [X] T107 [P] [US4] Implement bulk upload route in Stack A `server/routes/applications.ts` — POST /api/jobs/:jobId/applications/upload with MIME type validation (pdf, docx, md, txt, jpg), size check (50MB max), SHA-256 fingerprint, duplicate detection per FR-007, FR-008
- [X] T108 [P] [US4] Implement upload dialog UI in Stack A `src/components/UploadApplicationsDialog.tsx` — drag-and-drop + file picker, per-file validation errors, progress indicator, duplicate warnings per US4 scenarios 1–4
- [X] T109 [P] [US4] Implement `UploadApplicationsCommand` in Stack B `dotnet/src/Application/Applications/Commands/UploadApplicationsCommand.cs` — validate MIME, compute SHA-256, detect duplicates, create Application + ApplicationDocument records per FR-007, FR-008
- [X] T110 [P] [US4] Wire up upload endpoint in Stack B `dotnet/src/Web.Server/Endpoints/ApplicationEndpoints.cs` — POST /api/jobs/:jobId/applications/upload
- [X] T111 [US4] Implement Blazor upload dialog in Stack B `dotnet/src/Web.Client/Components/UploadApplicationsDialog.razor` — file selection, validation feedback, progress per FR-023

**Checkpoint**: Applications can be uploaded in both stacks with validation and dedup.

---

## Phase 8: User Story 5 — AI scoring pipeline runs and results are visible (Priority: P2)

**Goal**: Extraction → multi-run scoring → aggregation pipeline with configurable runs, variance detection, and failure handling

**Independent Test**: Trigger scoring for a single application; verify N scoring runs with scores, evidence, must-haves, AI model metadata; verify aggregated result with final decision.

**Dependencies**: Requires US3a (production-approved prompt) and US4 (uploaded applications)

### Implementation for User Story 5

- [X] T112 [P] [US5] Implement extraction worker in Stack A `server/workers/extraction.ts` — extract document to normalised markdown via `AWR_SEQ_API_ENDPOINT/assess/passthrough` with AWR auth, store ExtractionArtifact with confidence score per US5 scenario 1
- [X] T113 [P] [US5] Implement scoring worker in Stack A `server/workers/scoring.ts` — resolve prompt placeholders ({{JOB_SPEC_TEXT}}, {{CANDIDATE_CV_TEXT}}), call passthrough API with AWR auth, parse LLM JSON response (eligibility_gate, rubric_scores, composite_score, notes, improvement_recommendations) into ScoringRun per FR-045, FR-047, scoring-passthrough.md
- [X] T114 [P] [US5] Implement aggregation worker in Stack A `server/workers/aggregation.ts` — aggregate N ScoringRuns using configured strategy (median/mean/weighted), compute variance, determine decision (Eligible/Excluded/NeedsManualReview), flag if variance > threshold per FR-011, FR-012
- [X] T115 [US5] Implement pipeline orchestrator in Stack A `server/services/pipeline.ts` — coordinate Queued → Extracting → Scoring (N runs) → Aggregating → Completed/NeedsManualReview, retry with backoff on failure, move to DLQ after max retries per FR-013
- [X] T116 [P] [US5] Implement process job route in Stack A `server/routes/jobs.ts` — POST /api/jobs/:jobId/process triggering pipeline for queued applications, check production-approved prompt exists per FR-032, FR-040
- [X] T117 [P] [US5] Implement `ScoreApplicationCommand` in Stack B `dotnet/src/Application/Scoring/Commands/ScoreApplicationCommand.cs` — resolve prompt, call LlmProxyService.ScoreAsync, parse response per FR-045
- [X] T118 [P] [US5] Implement extraction command in Stack B `dotnet/src/Application/Scoring/Commands/ExtractApplicationCommand.cs` — call LlmProxyService for doc extraction
- [X] T119 [P] [US5] Implement aggregation command in Stack B `dotnet/src/Application/Scoring/Commands/AggregateResultsCommand.cs` — N-run aggregation with strategy, variance, decision
- [X] T120 [US5] Implement `ProcessJobCommand` in Stack B `dotnet/src/Application/Jobs/Commands/ProcessJobCommand.cs` — orchestrate pipeline for all queued applications per job
- [X] T121 [US5] Wire up process endpoint in Stack B `dotnet/src/Web.Server/Endpoints/JobEndpoints.cs` — POST /api/jobs/:jobId/process with production-approved prompt check

**Checkpoint**: Complete AI scoring pipeline works end-to-end in both stacks. Applications flow from Queued through scoring to final decision.

---

## Phase 9: User Story 6 — Recruiter views ranked lists and drills into application detail (Priority: P2)

**Goal**: Longlist/shortlist/exclusions tabs with sorting, filtering, and detailed application drill-down

**Independent Test**: Navigate to completed job; verify correct categorisation in tabs; drill into one application with all run details.

**Dependencies**: Requires US5 (scored applications)

### Implementation for User Story 6

- [X] T122 [P] [US6] Implement application listing route in Stack A `server/routes/applications.ts` — GET /api/jobs/:jobId/applications with status/list/sort/variance/pagination query params, exclude testRunId applications per api-stack-a.md
- [X] T123 [P] [US6] Implement application detail routes in Stack A `server/routes/applications.ts` — GET /api/applications/:applicationId (full detail), GET .../runs, GET .../result, GET .../extraction per api-stack-a.md
- [X] T124 [US6] Implement ranked list view in Stack A `src/components/JobDetailView.tsx` — longlist/shortlist/exclusions tabs with counts, sortable columns (score, name, variance), variance filter per US6 scenarios 1–3
- [X] T125 [US6] Implement application detail view in Stack A `src/components/ApplicationDetail.tsx` — documents with download, extracted text, all N scoring runs with per-category breakdown and evidence, aggregated decision with rationale and improvement tips per US6 scenarios 4–5
- [X] T126 [P] [US6] Implement `GetApplicationsQuery` in Stack B `dotnet/src/Application/Applications/Queries/GetApplicationsQuery.cs` — filter, sort, paginate, exclude test cases
- [X] T127 [P] [US6] Implement `GetApplicationDetailQuery`, `GetScoringRunsQuery`, `GetAggregatedResultQuery`, `GetExtractionArtifactQuery` in Stack B `dotnet/src/Application/Applications/Queries/`
- [X] T128 [US6] Wire up application endpoints in Stack B `dotnet/src/Web.Server/Endpoints/ApplicationEndpoints.cs` — GET list, GET detail, GET runs, GET result, GET extraction
- [X] T129 [US6] Implement Blazor ApplicationList component in Stack B `dotnet/src/Web.Client/Components/ApplicationList.razor` — longlist/shortlist/exclusions tabs with sorting and filtering
- [X] T130 [US6] Implement Blazor ApplicationDetail page in Stack B `dotnet/src/Web.Client/Pages/ApplicationDetail.razor` — documents, extracted text, scoring runs, aggregated result per FR-025

**Checkpoint**: Recruiters can view ranked candidates and drill into full application details in both stacks.

---

## Phase 10: User Story 7 — Recruiter performs a manual review (Priority: P3)

**Goal**: Three-pane manual review interface (job spec | rubric scoring | application content) with per-category scoring, comments, and immutable audit trail

**Independent Test**: Open manual review for flagged application; allocate points in all categories; save; reload and verify persistence with audit trail entries.

### Implementation for User Story 7

- [X] T131 [P] [US7] Implement manual review save/get routes in Stack A `server/routes/applications.ts` — GET /api/applications/:applicationId/manual-review, POST /api/applications/:applicationId/manual-review with audit trail append per FR-014, api-stack-a.md
- [X] T132 [US7] Implement three-pane manual review UI in Stack A `src/components/ManualReviewView.tsx` — left pane: job spec and rubric categories; centre pane: per-category scoring form with point allocation, live weighted score recalculation, overall comment; right pane: application content (extracted text and documents); resizable panes per US7 scenarios 1–2
- [X] T133 [US7] Implement audit trail display in Stack A `src/components/ManualReviewView.tsx` — chronological list of score changes with reviewer, timestamp, category, previous/new values per US7 scenario 5
- [X] T134 [US7] Add test-case pre-filtering to manual review in Stack A — when launched from TestRunResultsDialog (US3a), filter to only show test applications from that specific test run per FR-052, US3a scenario 13
- [X] T135 [P] [US7] Implement `SaveManualReviewCommand` in Stack B `dotnet/src/Application/Applications/Commands/SaveManualReviewCommand.cs` — validate rubric scores, append audit entry, compute adjusted score per FR-014, FR-026
- [X] T136 [P] [US7] Implement `GetManualReviewQuery` in Stack B `dotnet/src/Application/Applications/Queries/GetManualReviewQuery.cs`
- [X] T137 [US7] Wire up manual review endpoints in Stack B `dotnet/src/Web.Server/Endpoints/ApplicationEndpoints.cs` — GET and POST manual-review per api-stack-b.md
- [X] T138 [US7] Implement Blazor ManualReview page in Stack B `dotnet/src/Web.Client/Pages/ManualReview.razor` — three resizable panes, per-category rubric scoring with point allocation, live score recalculation, audit trail display per FR-026, US7 scenarios 1–5
- [X] T139 [US7] Add test-case pre-filtering to Blazor manual review — filter by testRunId when launched from prompt test results per FR-052

**Checkpoint**: Manual review works end-to-end in both stacks with audit trail. Test-case review integrates with US3a prompt approval workflow.

---

## Phase 11: User Story 8 — Admin/recruiter monitors the processing pipeline (Priority: P3)

**Goal**: Dashboard stats with auto-refresh, pipeline visualiser, failure queue with retry, department/org filtering

**Independent Test**: While jobs process, verify stats auto-refresh within 30s; simulate failure and verify it appears in DLQ with retry option.

### Implementation for User Story 8

- [X] T140 [P] [US8] Implement stats route in Stack A `server/routes/stats.ts` — GET /api/stats returning SystemStats (totalJobs, activeJobs, totalApplications, queued/processing/completed/failed counts, avgThroughputPerHour) per api-stack-a.md
- [X] T141 [P] [US8] Implement DLQ routes in Stack A `server/routes/dlq.ts` — GET /api/dlq (list failure queue items), POST /api/dlq/:itemId/retry (retry failed item) per api-stack-a.md
- [X] T142 [P] [US8] Implement audit route in Stack A `server/routes/audit.ts` — GET /api/audit with entityType/eventType/date/pagination filters per api-stack-a.md
- [X] T143 [US8] Add auto-refresh (30s polling) to dashboard stats in Stack A `src/components/DashboardView.tsx` — use TanStack Query refetchInterval per SC-003, FR-015
- [X] T144 [US8] Implement pipeline visualiser in Stack A `src/components/PipelineVisualiser.tsx` — per-job stages (extraction → scoring → aggregation → complete) with counts and progress percentage per US8 scenario 4
- [X] T145 [US8] Implement failure queue browser in Stack A `src/components/FailureQueueView.tsx` — list DLQ items with error details, failure reason, retry count, timestamps, retry button, 30s auto-refresh per US8 scenario 3
- [X] T146 [US8] Add department/organisation filtering to dashboard in Stack A `src/components/DashboardView.tsx` per US8 scenario 2
- [X] T147 [P] [US8] Implement `GetStatsQuery` in Stack B `dotnet/src/Application/Stats/Queries/GetStatsQuery.cs`
- [X] T148 [P] [US8] Implement `GetDlqItemsQuery` and `RetryDlqItemCommand` in Stack B `dotnet/src/Application/Dlq/`
- [X] T149 [P] [US8] Implement `GetAuditEventsQuery` in Stack B `dotnet/src/Application/Audit/Queries/GetAuditEventsQuery.cs`
- [X] T150 [US8] Wire up stats, DLQ, and audit endpoints in Stack B `dotnet/src/Web.Server/Endpoints/` — StatsEndpoints.cs, DlqEndpoints.cs, AuditEndpoints.cs per api-stack-b.md
- [X] T151 [US8] Implement Blazor dashboard stats with 30s auto-refresh in Stack B `dotnet/src/Web.Client/Pages/Dashboard.razor` per SC-003, FR-015
- [X] T152 [US8] Implement Blazor FailureQueue component in Stack B `dotnet/src/Web.Client/Components/FailureQueue.razor` — DLQ items, retry, 30s refresh per FR-027

**Checkpoint**: Full operational monitoring in both stacks with auto-refreshing stats and failure recovery.

---

## Phase 12: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories and final integration

- [X] T153 [P] Implement error handling middleware in Stack A `server/middleware/error-handler.ts` — consistent error response format, logging
- [X] T154 [P] Implement request validation middleware in Stack A `server/middleware/validate.ts` — Zod schema validation for all POST/PUT routes
- [X] T155 [P] Implement `LlmProxyService` in Stack B `dotnet/src/Infrastructure/Services/LlmProxyService.cs` — passthrough calls with AwrAuthHandler, extraction, prompt generation, scoring per contracts/scoring-passthrough.md
- [X] T156 [P] Implement LLM proxy route in Stack A `server/routes/llm.ts` — POST /api/llm server-side LLM proxy per FR-017
- [X] T157 [P] Add API client functions for all new endpoints in Stack A `src/lib/api.ts` — prompt CRUD, test-run CRUD, stats, DLQ, audit
- [X] T158 [P] Add mock API implementations for prompt and test-run endpoints in Stack A `src/lib/api-mock.ts` — for offline development
- [X] T159 Implement FR-030 feedback patterns in Stack B Blazor — check API response before updating state, show success/error messages, don't close dialogs on failure per FR-030
- [X] T160 [P] Add accessible navigation entry points in Stack B Blazor layout per FR-023 — "Create Job" on Dashboard, "Upload Applications" on JobDetail, User Management from UserMenu, Failure Queue from navigation
- [X] T161 Run quickstart.md validation — verify both stacks start and serve correctly per quickstart.md instructions
- [X] T162 Verify Stack A and Stack B API contract parity — ensure all endpoints return identical response shapes per plan.md parity requirement
- [X] T163 Verify hash consistency between CreateUserCommand and AuthEndpoints login in Stack B per FR-031

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion — BLOCKS all user stories
- **US1 (Phase 3)**: Depends on Foundational — gate to all other stories
- **US2 (Phase 4)**: Depends on US1 (auth system must exist)
- **US3 (Phase 5)**: Depends on US1 (auth required for job creation)
- **US3a (Phase 6)**: Depends on US3 (job with approved rubric must exist)
- **US4 (Phase 7)**: Depends on US1 (auth required), independent of US3/US3a for upload; US3a needed for scoring
- **US5 (Phase 8)**: Depends on US3a (production-approved prompt) and US4 (uploaded applications)
- **US6 (Phase 9)**: Depends on US5 (scored applications needed for ranked lists)
- **US7 (Phase 10)**: Depends on US1 (auth); integrates with US3a (test-case review) and US6 (manual review entry)
- **US8 (Phase 11)**: Depends on US1 (auth); can be built in parallel with US4–US7
- **Polish (Phase 12)**: Can begin after Foundational; finalize after all desired stories complete

### User Story Dependencies

```
US1 (Auth) ─────────────────┬──► US2 (User Mgmt)
                            ├──► US3 (Job Creation) ──► US3a (Prompts) ──► US5 (Scoring) ──► US6 (Lists)
                            ├──► US4 (Upload) ─────────────────────────────► US5 (Scoring)
                            ├──► US7 (Manual Review) ◄── US3a (test review), US6 (drill-down)
                            └──► US8 (Monitoring)
```

### Within Each User Story

- Models/entities before services/commands
- Services/commands before routes/endpoints
- Backend before frontend (API must exist for UI)
- Stack A and Stack B implementations for the same story can run in parallel
- Core implementation before integration points

### Parallel Opportunities

- All Setup tasks marked [P] can run in parallel (T002–T005)
- All Foundational tasks marked [P] can run in parallel (T006–T022)
- Stack A and Stack B implementations within each user story run in parallel
- US2 and US3 can start in parallel once US1 is complete
- US4 and US3a can progress in parallel (upload doesn't need prompts; prompts don't need uploads)
- US7 and US8 can be built in parallel with other stories once US1 is complete

---

## Parallel Example: User Story 3a

```bash
# Launch all Stack A backend prompt routes together:
Task T067: "Prompt CRUD routes in server/routes/prompts.ts"
Task T068: "Prompt edit route in server/routes/prompts.ts"
Task T069: "Prompt activate route in server/routes/prompts.ts"
Task T070: "Prompt rate route in server/routes/prompts.ts"
Task T071: "Prompt generate route in server/routes/prompts.ts"

# Launch all Stack B CQRS commands together:
Task T087: "CreatePromptCommand"
Task T088: "EditPromptCommand"
Task T089: "ActivatePromptCommand"
Task T090: "RatePromptCommand"
Task T091: "GeneratePromptCommand"
Task T093: "GetPromptsQuery / GetPromptQuery"
```

## Parallel Example: Foundational Phase

```bash
# All Stack A type definitions:
Task T006: "ScoringPrompt interface in src/types/index.ts"
Task T007: "PromptTestRun interface in src/types/index.ts"
Task T008: "testRunId on Application in src/types/index.ts"

# All Stack B domain entities (fully parallel):
Task T009: "ScoringPrompt entity"
Task T010: "PromptStatus enum"
Task T011: "PromptSource enum"
Task T012: "PromptTestRun entity"
Task T013: "TestRunStatus enum"
Task T014: "testRunId on Application"
Task T015: "IScoringPromptRepository"
Task T016: "IPromptTestRunRepository"
```

---

## Implementation Strategy

### MVP First (US1 + US2 Only)

1. Complete Phase 1: Setup (AWR auth infra)
2. Complete Phase 2: Foundational (new entities, storage)
3. Complete Phase 3: User Story 1 — Auth + Dashboard
4. Complete Phase 4: User Story 2 — User Management
5. **STOP and VALIDATE**: Login, create users, department filtering all work in both stacks
6. Deploy/demo if ready

### Incremental Delivery

1. Setup + Foundational → Foundation ready
2. US1 (Auth) → Test independently → **MVP Gate 1** (users can log in)
3. US2 (User Mgmt) → Test independently → **MVP Gate 2** (admin can manage users)
4. US3 (Job Creation) → Test independently → Jobs exist with rubrics
5. US3a (Prompt Mgmt) → Test independently → Prompts created, tested, approved
6. US4 (Upload) → Test independently → Applications ingested
7. US5 (Scoring) → Test independently → Full pipeline runs
8. US6 (Ranked Lists) → Test independently → Recruiters see results
9. US7 (Manual Review) + US8 (Monitoring) → Test independently → Full platform
10. Polish → Final validation → Production ready

### Parallel Team Strategy

With multiple developers:

1. Team completes Setup + Foundational together
2. Once Foundational is done:
   - **Developer A (Stack A)**: US1 → US3 → US3a frontend
   - **Developer B (Stack B)**: US1 → US3 → US3a Blazor
   - **Developer C (Stack A)**: US2 → US4 → US5 backend
   - **Developer D (Stack B)**: US2 → US4 → US5 backend
3. After core stories: US6, US7, US8 can be distributed across the team

---

## Notes

- [P] tasks = different files, no dependencies on incomplete tasks
- [Story] label maps task to specific user story for traceability
- Each user story should be independently completable and testable
- Both stacks must maintain API contract parity (same request/response shapes)
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- AWR auth headers (T002/T003) must be included in ALL calls to `AWR_SEQ_API_ENDPOINT`
- Test-case applications (with testRunId) are ALWAYS excluded from production ranked lists
- Prompt versioning is immutable — edits create new revisions, never modify originals
