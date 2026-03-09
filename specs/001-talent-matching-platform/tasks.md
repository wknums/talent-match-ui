# Tasks: Talent Matching Platform

**Input**: Design documents from `/specs/001-talent-matching-platform/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅ (api-stack-a.md, api-stack-b.md)

**Tests**: Not explicitly requested in the feature specification. Testing infrastructure and minimum-coverage tests are included as part of the Polish phase per Milestone D of the plan.

**Organization**: Tasks are grouped by user story (US1–US8, including US3a) to enable independent implementation and testing. Stack B (Blazor/.NET Clean Architecture) tasks are integrated alongside Stack A tasks within each user story phase.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3, US3a)
- Include exact file paths in descriptions

## Path Conventions

- **Stack A**: `server/` (Express backend) + `src/` (React frontend) at repository root
- **Stack B**: `dotnet/src/` (Clean Architecture .NET solution) — separate directory to coexist with Stack A

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization, configuration, and shared utilities

- [x] T001 Create API mode configuration by adding `API_MODE=mock|real` environment variable to `.env` and reading it in `server/index.ts` so the frontend can query which mode is active
- [x] T002 [P] Create KV key constants and pattern helpers in `server/storage/kv-keys.ts` defining all key patterns from the plan (`auth:users`, `auth:current-user`, `auth:reset-requests`, `jobs`, `job:{jobId}:versions`, `job:{jobId}:applications`, `app:{applicationId}:documents`, `app:{applicationId}:extraction`, `app:{applicationId}:runs`, `app:{applicationId}:result`, `app:{applicationId}:manual-review`, `dlq`, `ledger`, `system:stats`)
- [x] T003 [P] Create typed KV data helper utilities in `server/storage/kv-helpers.ts` wrapping StorageProvider with `getArray<T>`, `setArray<T>`, `pushToArray<T>`, `removeFromArray<T>`, `getOrDefault<T>` for safe typed access to KV collections
- [x] T004 [P] Create request validation middleware in `server/middleware/validate.ts` using Zod schemas for body, params, and query validation with structured JSON error responses

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T005 Create server-side session management and auth verification middleware in `server/middleware/auth.ts` that reads session from KV (`auth:current-user`), attaches the authenticated user to the Express request object, and returns 401 for unauthenticated requests
- [x] T006 [P] Create role-based access control middleware in `server/middleware/rbac.ts` exporting `requireRole('admin')` and `requireRole('recruiter')` Express middleware guards that check the user attached by the auth middleware
- [x] T007 [P] Create centralized error handling middleware in `server/middleware/error-handler.ts` that catches all route errors and returns structured JSON responses (`{ error, message, statusCode }`) with server-side error logging
- [x] T008 [P] Create audit ledger service in `server/services/audit.ts` exporting `appendEvent(actor, eventType, entityType, entityId, payload, correlationId)` that appends a `ProcessingEvent` entry to the `ledger` KV key with a generated timestamp and correlation ID
- [x] T009 Register all new route modules and middleware in `server/index.ts`: mount auth, users, jobs, applications, stats, audit, and DLQ routers under `/api/`; apply auth middleware globally (except login); apply error handler last
- [x] T010 Refactor `src/lib/api.ts` to create an API client factory: extract existing mock functions into `src/lib/api-mock.ts`, create a new `src/lib/api-real.ts` module for real API calls, and export a single `api` object from `src/lib/api.ts` that delegates to mock or real implementation based on the `API_MODE` environment variable

**Checkpoint**: Foundation ready — user story implementation can now begin in parallel

---

## Phase 3: User Story 1 — Recruiter authenticates and reaches dashboard (Priority: P1) 🎯 MVP

**Goal**: A recruiter logs in with username and password and lands on a dashboard filtered to show only their department's jobs. An admin sees all jobs.

**Independent Test**: Login with admin credentials → verify all-department dashboard. Create a recruiter scoped to "Engineering" → log in → verify only Engineering jobs appear.

### Implementation for User Story 1

- [x] T011 [P] [US1] Create auth API routes in `server/routes/auth.ts`: POST `/api/auth/login` (verify credentials via SHA-256 hash comparison against `auth:users` KV, store session in `auth:current-user`), POST `/api/auth/logout` (clear session from KV), GET `/api/auth/me` (return current user from session), POST `/api/auth/change-password` (verify current password hash, update hash in `auth:users`)
- [x] T012 [P] [US1] Create default admin user seeding in `server/services/init-users.ts` that checks `auth:users` KV on server start and seeds an admin account (username=admin, SHA-256 hashed password, role=admin, department=all) if no users exist
- [x] T013 [US1] Implement real API auth functions in `src/lib/api-real.ts`: `login(username, password)`, `logout()`, `getCurrentUser()`, `changePassword(currentPassword, newPassword)` calling the server auth routes from T011
- [x] T014 [US1] Update `src/components/LoginForm.tsx` to use the `api` client (from T010) instead of direct `src/lib/auth.ts` calls; display server-returned error messages for invalid credentials; redirect to dashboard on success
- [x] T015 [US1] Update `src/App.tsx` to call `api.getCurrentUser()` on mount to restore session; store user state in React context or top-level state; redirect to login screen if no active session exists; pass user role and department to child components
- [x] T016 [US1] Implement department-filtered job display in `src/components/DashboardView.tsx` so that admin users see all departments' jobs and recruiter users see only their assigned department's jobs, using the role and department from the authenticated user context

**Checkpoint**: User Story 1 is fully functional — login, session persistence, role-based dashboard filtering

---

## Phase 4: User Story 2 — Admin manages users and passwords (Priority: P1)

**Goal**: Admin can create users, reset any user's password, delete users, and approve/reject recruiter password-reset requests. All users can change their own password.

**Independent Test**: As admin, create a recruiter user → reset their password → approve a pending password-reset request from a different user.

### Implementation for User Story 2

- [x] T017 [P] [US2] Create user management API routes in `server/routes/users.ts`: GET `/api/users` (admin: return all users from `auth:users` KV without password hashes), POST `/api/users` (admin: create user with username, role, department, initial password hash; append to `auth:users`), DELETE `/api/users/:userId` (admin: remove user; block self-deletion), POST `/api/users/:userId/reset-password` (admin: update password hash in `auth:users`)
- [x] T018 [P] [US2] Create password-reset request routes in `server/routes/users.ts`: GET `/api/users/reset-requests` (admin: list pending from `auth:reset-requests` KV), POST `/api/users/reset-requests` (recruiter: submit reset request with reason), PUT `/api/users/reset-requests/:requestId` (admin: approve with new password hash or reject; remove from pending list)
- [x] T019 [US2] Implement real API user management functions in `src/lib/api-real.ts`: `getAllUsers()`, `createUser(username, role, department, password)`, `deleteUser(userId)`, `resetUserPassword(userId, newPassword)`, `getPasswordResetRequests()`, `requestPasswordReset(reason)`, `resolvePasswordResetRequest(requestId, action, newPassword)` calling the routes from T017–T018
- [x] T020 [US2] Update `src/components/UserManagementDialog.tsx` to use the `api` client for all user operations (list, create, delete, reset password, view/approve/reject reset requests) instead of direct `src/lib/auth.ts` calls; add confirmation dialogs for destructive actions
- [x] T021 [US2] Update `src/components/ChangePasswordDialog.tsx` to call `api.changePassword()` instead of direct `src/lib/auth.ts` calls; validate current password field is required; show success/error feedback
- [x] T022 [US2] Update `src/components/UserMenu.tsx` to display current user info (name, role, department) from the authenticated user context and trigger logout via `api.logout()`

**Checkpoint**: User Stories 1 AND 2 are fully functional — authentication and complete user management

---

## Phase 5: User Story 3 — Recruiter creates a job with rubric and scoring config (Priority: P2)

**Goal**: Recruiter fills in job details (title, department, organisation, posting date), optionally
uploads a job specification document and/or a separate rubric document, reviews/approves or manually
defines a scoring rubric with weighted categories, sets must-have criteria and desired criteria, and
configures scoring runs and aggregation strategy. Document extraction uses the external API at
`AWR_SEQ_API_ENDPOINT`. Supported document types: pdf, jpg, md, txt, docx.

**Independent Test**: Create a job end-to-end → job card appears on dashboard with correct title,
department, organisation, and days since posting. Upload a job spec → verify fields auto-populated
including desired criteria. Upload a rubric doc with mismatched title → verify warning is shown.

### Implementation for User Story 3

- [x] T023 [P] [US3] Create job CRUD API routes in `server/routes/jobs.ts`: GET `/api/jobs` (list all jobs from `jobs` KV, filtered by requester's department for recruiters), POST `/api/jobs` (create job with title, department, organisation, posting date, status, and initial config version; append to `jobs` KV and create `job:{jobId}:versions`), GET `/api/jobs/:jobId` (return job with computed stats — application counts by status)
- [x] T024 [P] [US3] Create job configuration versioning route in `server/routes/jobs.ts`: PUT `/api/jobs/:jobId/config` (create a new `JobConfigVersion` with rubric categories, must-have criteria, desired criteria, scoring run count, aggregation strategy, longlist/shortlist thresholds, and variance threshold; append to `job:{jobId}:versions` without affecting in-progress scoring on previous versions)
- [x] T025 [US3] Implement real API job functions in `src/lib/api-real.ts`: `getJobs()`, `getJob(jobId)`, `createJob(jobData)`, `updateJobConfig(jobId, configData)` calling the server routes from T023–T024
- [x] T026 [US3] Update `src/components/CreateJobDialog.tsx` to use the `api` client for job creation; submit all fields (title, department, organisation, posting date) plus initial configuration (rubric, must-haves, scoring runs, aggregation strategy, thresholds); refresh dashboard on success
- [x] T027 [US3] Update `src/components/UploadRubricDialog.tsx` to extract rubric categories via POST `/api/llm` proxy, validate that weights sum to 1.0, present categories for user confirmation, and save via `api.updateJobConfig()` on confirmation
- [x] T028 [US3] Update `src/components/JobCard.tsx` to display the organisation name alongside the department and calculate/display days since posting from the `postingDate` field
- [x] T122 [P] [US3] Add `DesiredCriteria` interface to `src/types/index.ts` (with `id`, `qualification`, `description` fields) and add `desiredCriteria` array field to `JobConfigVersion` type; add optional `jobDescription` field to `Job` type
- [x] T123 [P] [US3] Update `server/routes/jobs.ts` POST `/api/jobs` and PUT `/api/jobs/:jobId/config` routes to accept and persist `desiredCriteria` and `jobDescription` fields alongside existing must-haves
- [x] T124 [P] [US3] Add `POST /api/jobs/extract-spec` route in `server/routes/jobs.ts` that accepts a multipart file upload (pdf, jpg, md, txt, docx), forwards the document to the external API at `AWR_SEQ_API_ENDPOINT` for extraction, and returns extracted job metadata (title, description, department, organisation, must-haves, desired criteria, and optionally rubric categories with weights)
- [x] T125 [P] [US3] Add `POST /api/jobs/extract-rubric` route in `server/routes/jobs.ts` that accepts a multipart rubric document upload (pdf, jpg, md, txt, docx), forwards to `AWR_SEQ_API_ENDPOINT`, and returns extracted job title and rubric categories with weights summing to 1.0
- [x] T126 [US3] Update `src/components/CreateJobDialog.tsx`: expand accepted file types to include `.jpg`, `.jpeg`, `.txt`; replace internal `llm()` calls with `POST /api/jobs/extract-spec` API calls; add desired criteria section (add/remove items with qualification and description); add separate rubric document upload button calling `POST /api/jobs/extract-rubric`; implement job title mismatch warning for rubric documents; implement auto-generation of draft rubric when no rubric found in spec and no separate rubric uploaded (60% weight to must-haves); include `desiredCriteria` and `jobDescription` in job submission payload
- [x] T127 [US3] Update `src/components/UploadRubricDialog.tsx`: expand accepted file types to include `.jpg`, `.jpeg`, `.txt`; replace `llm()` calls with `POST /api/jobs/extract-rubric` API call; add `jobTitle` prop for mismatch detection; display warning when extracted rubric job title differs from provided job title advising user to correct and re-upload
- [x] T128 [P] [US3] Update `src/lib/api-real.ts` and `src/lib/api-mock.ts` to add `extractJobSpec(file)` and `extractRubric(file)` functions calling the new extraction endpoints; update `createJob()` and `updateJobConfig()` to include `desiredCriteria` and `jobDescription` fields
- [x] T129 [P] [US3] Update `.NET` `JobConfigVersion` entity in `dotnet/src/Domain/Entities/JobConfigVersion.cs` to add `DesiredCriteriaJson` string property; update `Job` entity to add optional `JobDescription` property; generate EF Core migration
- [x] T130 [US3] Update `.NET` `CreateJobCommand`, `CreateJobDto`, `CreateJobRequest`, `UpdateConfigDto`, `UpdateJobConfigRequest` and `JobsEndpoints.cs` to accept and persist `desiredCriteria` and `jobDescription` fields
- [x] T131 [US3] Update `dotnet/src/Web.Client/Components/CreateJobDialog.razor`: add file upload capability for job specification documents (pdf, jpg, md, txt, docx) with `AWR_SEQ_API_ENDPOINT` extraction via backend proxy; add separate rubric document upload with job title mismatch warning; add desired criteria section; implement auto-generated draft rubric when no rubric found

**Checkpoint**: Jobs can be created with full rubric and scoring configuration including document upload with extraction via AWR_SEQ_API_ENDPOINT, desired criteria, and rubric mismatch warnings; job cards display on dashboard

---

## Phase 6: User Story 3a — Recruiter creates, tests, and approves a scoring prompt (Priority: P1)

**Goal**: After a job is created with an approved rubric (US3), the recruiter creates, tests, and
approves a scoring prompt before production scoring can begin. Prompts are versioned, rated, and
must pass a test-and-review workflow to be approved for production. This is a hard prerequisite for
the scoring pipeline (US5) — production scoring MUST NOT proceed without an approved prompt.

**Independent Test**: Create a job with rubric → generate a draft prompt via API → edit, save, and
activate the prompt → upload 3 test applications → verify they are scored and flagged as test cases →
perform manual review on each → approve the prompt for production → verify production scoring can
proceed and test-case results do not appear in production ranked lists.

**Dependencies**: Requires US3 (job with rubric must exist). Required by US5 (pipeline gating).

### Setup for User Story 3a

- [x] T132 [P] [US3a] Add ScoringPrompt and PromptTestRun interfaces plus PromptStatus, PromptSource, and TestRunStatus enums to `src/types/index.ts` per data-model.md: ScoringPrompt (promptId, jobId, versionNumber, promptText, status, createdAt, lastModifiedAt, author, rating, comments, source, generationMetadata), PromptTestRun (testRunId, jobId, promptId, status, applicationIds, createdAt, completedAt, reviewedBy, reviewNotes); add optional `testRunId?: string` field to Application interface
- [x] T133 [P] [US3a] Add KV key patterns for prompts in `server/storage/kv-keys.ts`: `job:{jobId}:prompts` (ScoringPrompt[]) and `prompt-test-run:{testRunId}` (PromptTestRun) key generators; export helper functions `promptsKey(jobId)` and `promptTestRunKey(testRunId)`

### Stack A Server Routes for User Story 3a

- [x] T134 [US3a] Create prompt CRUD routes in `server/routes/prompts.ts`: GET `/api/jobs/:jobId/prompts` (list all prompt revisions from `job:{jobId}:prompts` KV sorted by versionNumber desc), POST `/api/jobs/:jobId/prompts` (create new prompt with auto-incremented versionNumber, status=draft, source from request body; validate job exists and has approved rubric per FR-033), GET `/api/jobs/:jobId/prompts/:promptId` (return single prompt revision), PUT `/api/jobs/:jobId/prompts/:promptId` (create a NEW revision with incremented versionNumber and updated promptText; original remains unchanged per FR-035)
- [x] T135 [US3a] Add prompt activation and rating routes in `server/routes/prompts.ts`: POST `/api/jobs/:jobId/prompts/:promptId/activate` (set this prompt to status=active, set any previously active prompt for this job to status=inactive in a single operation per FR-036; record state transition in audit trail), POST `/api/jobs/:jobId/prompts/:promptId/rate` (accept rating integer 0-5 and optional comments string; persist against the specific revision per FR-037)
- [x] T136 [US3a] Add prompt generation route in `server/routes/prompts.ts`: POST `/api/jobs/:jobId/prompts/generate` — read the job's approved rubric (categories, weights, must-haves, desired criteria) from `job:{jobId}:versions` KV, construct a system prompt instructing the AI to produce a structured scoring prompt based on the rubric, call `AWRSEQAPI_ENDPOINT/assess/passthrough` with the rubric context (reusing the existing passthrough pattern from `server/routes/jobs.ts` extract-spec/extract-rubric), return `{ promptText, generationMetadata }` per FR-034
- [x] T137 [US3a] Add prompt test run routes in `server/routes/prompts.ts`: POST `/api/jobs/:jobId/prompts/:promptId/test-runs` (accept file uploads like US4 upload flow — validate types/sizes, compute SHA-256 fingerprints, create Application records with `testRunId` set, create PromptTestRun linking applications to prompt revision, trigger pipeline for test applications), GET `/api/jobs/:jobId/prompts/:promptId/test-runs` (list test runs for prompt), GET `/api/jobs/:jobId/prompts/:promptId/test-runs/:testRunId` (return test run with application details and statuses)
- [x] T138 [US3a] Add test run approval and production approval routes in `server/routes/prompts.ts`: POST `/api/jobs/:jobId/prompts/:promptId/test-runs/:testRunId/approve` (verify ALL test applications have completed manual review without score changes per FR-039; set test run status to `approved`; record in audit), POST `/api/jobs/:jobId/prompts/:promptId/approve-production` (verify a PromptTestRun for this prompt has status=approved per FR-040; set prompt status to `production-approved`; deactivate any previously production-approved prompt for this job; record in audit)
- [x] T139 [US3a] Register prompt routes in `server/index.ts`: mount `createPromptsRouter(storage)` under protected routes; ensure authentication middleware is applied

### Stack A API Client for User Story 3a

- [x] T140 [P] [US3a] Add prompt API functions to `src/lib/api-real.ts`: `getPrompts(jobId)`, `createPrompt(jobId, { promptText, source, generationMetadata })`, `getPrompt(jobId, promptId)`, `editPrompt(jobId, promptId, { promptText })`, `activatePrompt(jobId, promptId)`, `ratePrompt(jobId, promptId, { rating, comments })`, `generatePrompt(jobId)`, `approvePromptForProduction(jobId, promptId)`, `createTestRun(jobId, promptId, files)`, `getTestRuns(jobId, promptId)`, `getTestRun(jobId, promptId, testRunId)`, `approveTestRun(jobId, promptId, testRunId)` — all calling the server routes from T134–T138
- [x] T141 [P] [US3a] Add mock prompt data generators and functions to `src/lib/api-mock.ts` for mock API mode: `generateMockPrompts()` producing sample prompt revisions with varying statuses, versions, and ratings; implement all prompt API functions with mock data behaviour matching the real API signatures

### Stack A UI Components for User Story 3a

- [x] T142 [US3a] Create `src/components/PromptManagement.tsx` component with prompt revision list, create/edit/import controls, and lifecycle management: display a dropdown/list of all prompt revisions for the job (version number, creation date, status badge, rating stars) fetched via `api.getPrompts(jobId)`; provide three creation methods — "Write Manually" (opens text editor), "Import File" (file upload that loads content into editor), "Generate from Rubric" (calls `api.generatePrompt(jobId)` and loads result into editor for review); save button creates a new revision via `api.createPrompt()` or `api.editPrompt()`; "Activate" button calls `api.activatePrompt()` with confirmation of deactivating previous; rating input (0-5 stars) and comments textarea that save via `api.ratePrompt()`; display production approval status prominently
- [x] T143 [US3a] Add prompt testing workflow to `src/components/PromptManagement.tsx` (or create a child `PromptTestWorkflow.tsx` component): "Test Prompt" button (enabled only when a prompt is active) opens a file upload interface for test applications using the same drag-and-drop pattern as `UploadApplicationsDialog.tsx`; display test run status with application list showing scoring progress; link each test application to ManualReviewView (US7) for review; show "Approve for Production" button (enabled only when all test cases have completed manual review without score changes per FR-039); on approval, call `api.approveTestRun()` then `api.approvePromptForProduction()` with success/error feedback
- [x] T144 [US3a] Integrate PromptManagement into `src/components/JobDetailView.tsx`: add a "Prompt Management" tab or section that renders the PromptManagement component; disable all prompt controls with explanatory message when the job has no approved rubric (FR-033); show warning banner when no production-approved prompt exists for the job; display current prompt status (draft/active/production-approved) in job header area

### Stack A Pipeline Integration for User Story 3a

- [x] T145 [US3a] Update `server/services/pipeline.ts` to enforce prompt production approval gate: before starting scoring for a job, query `job:{jobId}:prompts` KV for a prompt with status=`production-approved`; if none exists, reject processing with a clear error message per FR-032/FR-040; pass the production-approved promptId to the scoring worker
- [x] T146 [US3a] Update scoring worker in `server/workers/scoring.ts` to record `promptVersionId` field in each ScoringRun from the production-approved prompt's promptId; ensure test-run scoring also records the prompt version
- [x] T147 [US3a] Update GET `/api/jobs/:jobId/applications` in `server/routes/applications.ts` to exclude applications where `testRunId` is not null from production list results by default (FR-038); add optional `includeTestCases=true` query parameter to override for prompt testing views
- [x] T148 [US3a] Update `src/components/JobDetailView.tsx` process applications button: disable "Process Applications" when no production-approved prompt exists; show tooltip/message explaining that a prompt must be approved for production before scoring can begin (FR-032)

### Stack A Audit Trail for User Story 3a

- [x] T149 [P] [US3a] Add prompt lifecycle audit events throughout `server/routes/prompts.ts`: call audit service (`server/services/audit.ts`) to log events for `prompt.created`, `prompt.edited`, `prompt.activated`, `prompt.deactivated`, `prompt.rated`, `prompt.generated`, `prompt.test-run.created`, `prompt.test-run.approved`, `prompt.production-approved` — each event includes actor (userId), timestamp, entityType (`scoring_prompt` or `prompt_test_run`), entityId, and event-specific payload (FR-041)

### Stack B Domain Layer for User Story 3a

- [x] T150 [P] [US3a] Create `ScoringPrompt` entity in `dotnet/src/Domain/Entities/ScoringPrompt.cs` with properties: Id (Guid, PK), JobId (Guid, FK → Job), VersionNumber (int), PromptText (string, required), Status (PromptStatus enum), CreatedAt (DateTime), LastModifiedAt (DateTime), Author (string, userId), Rating (int?, nullable, 0-5), Comments (string?, nullable), Source (PromptSource enum), GenerationMetadataJson (string?, nullable for raw API response); add navigation property to Job
- [x] T151 [P] [US3a] Create `PromptTestRun` entity in `dotnet/src/Domain/Entities/PromptTestRun.cs` with properties: Id (Guid, PK), JobId (Guid, FK → Job), PromptId (Guid, FK → ScoringPrompt), Status (TestRunStatus enum), ApplicationIdsJson (string, serialized string[]), CreatedAt (DateTime), CompletedAt (DateTime?, nullable), ReviewedBy (string?, nullable userId), ReviewNotes (string?, nullable); add navigation properties
- [x] T152 [P] [US3a] Create prompt-related enums in `dotnet/src/Domain/Enums/`: `PromptStatus.cs` (Draft, Active, Inactive, ProductionApproved), `PromptSource.cs` (Manual, Imported, Generated), `TestRunStatus.cs` (PendingReview, Approved, Rejected)
- [x] T153 [P] [US3a] Create repository interfaces in `dotnet/src/Domain/Interfaces/`: `IScoringPromptRepository.cs` (GetByJobIdAsync, GetByIdAsync, AddAsync, UpdateAsync, GetActiveForJobAsync, GetProductionApprovedForJobAsync) and `IPromptTestRunRepository.cs` (GetByPromptIdAsync, GetByIdAsync, AddAsync, UpdateAsync)
- [x] T154 [US3a] Add optional `TestRunId` (Guid?, nullable FK → PromptTestRun) property to `Application` entity in `dotnet/src/Domain/Entities/Application.cs`; add navigation property to PromptTestRun

### Stack B Infrastructure for User Story 3a

- [x] T155 [US3a] Update `AppDbContext.cs` in `dotnet/src/Infrastructure/Persistence/`: add `DbSet<ScoringPrompt> ScoringPrompts` and `DbSet<PromptTestRun> PromptTestRuns`; add entity type configurations including indexes IX_ScoringPrompts_JobId_Status (composite on JobId + Status for single-active-per-job queries), IX_PromptTestRuns_PromptId, IX_Applications_TestRunId; configure Job → ScoringPrompts cascade delete; configure ScoringPrompt → PromptTestRuns cascade delete
- [x] T156 [US3a] Generate EF Core migration `AddScoringPromptsAndTestRuns` in `dotnet/src/Infrastructure/Persistence/Migrations/`: add ScoringPrompts table, PromptTestRuns table, and Applications.TestRunId nullable column with FK constraint per contracts/api-stack-b.md migration plan
- [x] T157 [P] [US3a] Implement `ScoringPromptRepository.cs` in `dotnet/src/Infrastructure/Persistence/Repositories/` implementing `IScoringPromptRepository`: GetByJobIdAsync (ordered by VersionNumber desc), GetActiveForJobAsync (single prompt with Status=Active for job), GetProductionApprovedForJobAsync, transactional activation (set new to Active, previous to Inactive)
- [x] T158 [P] [US3a] Implement `PromptTestRunRepository.cs` in `dotnet/src/Infrastructure/Persistence/Repositories/` implementing `IPromptTestRunRepository`: GetByPromptIdAsync, GetByIdAsync with application details join, AddAsync, UpdateAsync

### Stack B Application Layer for User Story 3a

- [x] T159 [P] [US3a] Implement prompt commands in `dotnet/src/Application/Prompts/Commands/`: `CreatePromptCommand.cs` (validate job has approved rubric, auto-increment version per job, set status=draft), `EditPromptCommand.cs` (create new revision with incremented version, preserve original per FR-035), `ActivatePromptCommand.cs` (set active, deactivate previous in transaction per FR-036, record audit event), `RatePromptCommand.cs` (validate rating 0-5, persist rating and comments per FR-037) — each with FluentValidation validators and MediatR handler
- [x] T160 [P] [US3a] Implement prompt generation command in `dotnet/src/Application/Prompts/Commands/GeneratePromptCommand.cs`: read job's approved rubric via IJobRepository, construct prompt-generation payload with rubric categories/weights/must-haves/desired criteria, call LlmProxyService to send to external API (matching Stack A's passthrough pattern), return generated prompt text and metadata per FR-034
- [x] T161 [P] [US3a] Implement production approval command in `dotnet/src/Application/Prompts/Commands/ApprovePromptForProductionCommand.cs`: verify a PromptTestRun for the prompt has status=Approved (FR-040), set prompt status to ProductionApproved, deactivate any previously production-approved prompt for the job, record audit event
- [x] T162 [P] [US3a] Implement prompt test run commands in `dotnet/src/Application/Prompts/Commands/`: `CreatePromptTestRunCommand.cs` (accept file uploads, create Application records with TestRunId, create PromptTestRun entity, trigger scoring pipeline for test applications), `ApprovePromptTestRunCommand.cs` (verify ALL test applications completed manual review without score changes per FR-039, set status=Approved, record audit)
- [x] T163 [P] [US3a] Implement prompt queries in `dotnet/src/Application/Prompts/Queries/`: `GetPromptsQuery.cs` (list all revisions for job ordered by version desc), `GetPromptQuery.cs` (single prompt by ID), `GetPromptTestRunsQuery.cs` (list test runs for prompt with application status details), `GetPromptTestRunQuery.cs` (single test run with full application details)
- [x] T164 [US3a] Update `GetApplicationsQuery.cs` in `dotnet/src/Application/Applications/Queries/` to filter out applications where TestRunId is not null from production list results by default (FR-038); accept optional includeTestCases parameter

### Stack B Web API Endpoints for User Story 3a

- [x] T165 [US3a] Create `PromptEndpoints.cs` in `dotnet/src/Web.Server/Endpoints/` with all prompt management endpoints per contracts/api-stack-b.md: GET `/api/jobs/{jobId}/prompts` → GetPromptsQuery, POST `/api/jobs/{jobId}/prompts` → CreatePromptCommand, GET `/api/jobs/{jobId}/prompts/{promptId}` → GetPromptQuery, PUT `/api/jobs/{jobId}/prompts/{promptId}` → EditPromptCommand, POST `/api/jobs/{jobId}/prompts/{promptId}/activate` → ActivatePromptCommand, POST `/api/jobs/{jobId}/prompts/{promptId}/rate` → RatePromptCommand, POST `/api/jobs/{jobId}/prompts/generate` → GeneratePromptCommand, POST `/api/jobs/{jobId}/prompts/{promptId}/approve-production` → ApprovePromptForProductionCommand
- [x] T166 [US3a] Add prompt test run endpoints to `PromptEndpoints.cs` (or create `PromptTestRunEndpoints.cs`) in `dotnet/src/Web.Server/Endpoints/`: POST `/api/jobs/{jobId}/prompts/{promptId}/test-runs` → CreatePromptTestRunCommand, GET `/api/jobs/{jobId}/prompts/{promptId}/test-runs` → GetPromptTestRunsQuery, GET `/api/jobs/{jobId}/prompts/{promptId}/test-runs/{testRunId}` → GetPromptTestRunQuery, POST `/api/jobs/{jobId}/prompts/{promptId}/test-runs/{testRunId}/approve` → ApprovePromptTestRunCommand — all with Authenticated authorization
- [x] T167 [US3a] Register prompt and test run endpoint groups in `dotnet/src/Web.Server/Program.cs`: call `app.MapPromptEndpoints()` (and `app.MapPromptTestRunEndpoints()` if separate) after existing endpoint registrations; ensure OpenAPI/Swagger documentation includes all new endpoints

### Stack B Blazor Client for User Story 3a

- [x] T168 [P] [US3a] Add prompt API methods to `dotnet/src/Web.Client/Services/ApiClient.cs`: GetPromptsAsync(jobId), CreatePromptAsync(jobId, promptText, source, metadata), EditPromptAsync(jobId, promptId, promptText), ActivatePromptAsync(jobId, promptId), RatePromptAsync(jobId, promptId, rating, comments), GeneratePromptAsync(jobId), ApprovePromptForProductionAsync(jobId, promptId), CreateTestRunAsync(jobId, promptId, files), GetTestRunsAsync(jobId, promptId), GetTestRunAsync(jobId, promptId, testRunId), ApproveTestRunAsync(jobId, promptId, testRunId) — typed request/response models matching the API contracts
- [x] T169 [US3a] Create `PromptManagement.razor` component in `dotnet/src/Web.Client/Components/`: prompt revision dropdown/list (version number, creation date, status badge, rating display), three creation methods (manual authoring textarea, file import with content loading, "Generate from Rubric" button calling GeneratePromptAsync), save/edit buttons, activate/deactivate toggle, rating input (0-5 integer or star selector), comments textarea, production approval status indicator — reference: Stack A `src/components/PromptManagement.tsx` behaviour
- [x] T170 [US3a] Create `PromptTestRunner.razor` component in `dotnet/src/Web.Client/Components/`: file upload zone for test applications (reuse UploadApplications patterns), test run status display with application list showing scoring progress, link to ManualReview.razor for each test application, "Approve for Production" button enabled only when test run has status=Approved and all test applications reviewed without changes — reference: Stack A prompt test workflow
- [x] T171 [US3a] Integrate prompt management into `dotnet/src/Web.Client/Pages/JobDetail.razor`: add "Prompt Management" section or tab embedding PromptManagement.razor and PromptTestRunner.razor components; disable all prompt controls with message when job has no approved rubric (FR-033); show warning banner when no production-approved prompt exists; gate "Process Applications" button on production-approved prompt status (FR-032)

### Stack B Audit and Pipeline Integration for User Story 3a

- [x] T172 [P] [US3a] Add ProcessingEvent audit recording to all prompt command handlers (T159–T162): log events via IProcessingEventRepository for `prompt.created`, `prompt.edited`, `prompt.activated`, `prompt.deactivated`, `prompt.rated`, `prompt.generated`, `prompt.test-run.created`, `prompt.test-run.approved`, `prompt.production-approved` with actor, timestamp, entityType, entityId, and event payload (FR-041)
- [x] T173 [US3a] Update `ProcessJobCommand.cs` in `dotnet/src/Application/Jobs/Commands/` (or equivalent pipeline trigger): before starting scoring, query IScoringPromptRepository for a production-approved prompt for the job; reject with clear error if none exists (FR-032/FR-040); pass promptId to scoring logic for recording in ScoringRun.PromptVersionId

**Checkpoint**: Prompt management is fully functional in both stacks — prompts can be created (manual/import/generate), versioned, activated, rated, tested with real applications, and approved for production. Production scoring is gated on prompt approval. Test-case results are excluded from production ranked lists.

---

## Phase 7: User Story 4 — Recruiter uploads applications in bulk (Priority: P2)

**Goal**: Recruiter selects an active job and uploads document files (drag-and-drop or bulk select). Each file is validated, fingerprinted, stored, and queued for processing.

**Independent Test**: Upload 5 application files → verify all appear in the job's application list with status "Queued" and correct SHA-256 fingerprints.

### Implementation for User Story 4

- [x] T029 [P] [US4] Create document storage service in `server/services/document-storage.ts` that accepts uploaded file buffers, computes SHA-256 fingerprints, stores files as base64 in KV under `app:{applicationId}:documents` with `ApplicationDocument` metadata (fingerprint, fileType, fileSize, fileName, uploadTimestamp), and detects duplicates by comparing fingerprints against existing documents for the job
- [x] T030 [P] [US4] Create application upload API route in `server/routes/applications.ts`: POST `/api/jobs/:jobId/applications/upload` with multipart file handling (multer), per-file type validation (PDF, DOCX, MD), size validation (max 2 MB), SHA-256 fingerprint generation via document-storage service, duplicate detection with warning, and creation of Application records with status "Queued" in `job:{jobId}:applications` KV
- [x] T031 [US4] Create application list and detail API routes in `server/routes/applications.ts`: GET `/api/jobs/:jobId/applications` (list with optional filters for status, score range, decision), GET `/api/applications/:applicationId` (full application detail with documents, status, scores)
- [x] T032 [US4] Implement real API application functions in `src/lib/api-real.ts`: `uploadApplications(jobId, files)`, `getApplications(jobId, filters)`, `getApplication(applicationId)` calling the routes from T030–T031
- [x] T033 [US4] Update `src/components/UploadApplicationsDialog.tsx` to use the `api` client for file upload; display per-file progress indicators, inline validation errors for invalid types/sizes, and duplicate fingerprint warnings with link-or-separate options; prevent interface timeout for large batches

**Checkpoint**: Applications can be bulk-uploaded, validated, fingerprinted, and queued for processing

---

## Phase 8: User Story 5 — AI scoring pipeline runs and results are visible (Priority: P2)

**Goal**: Queued applications are extracted to normalised text, scored N times by AI with evidence and must-have evaluation, then aggregated into a final decision (Eligible / Excluded / Needs Manual Review).

**Independent Test**: Trigger scoring for a single application → verify N scoring run records exist with scores, evidence citations, must-have results, and AI model metadata → verify aggregated result with final decision.

### Implementation for User Story 5

- [x] T034 [P] [US5] Create extraction worker in `server/workers/extraction.ts`: read application documents from `app:{id}:documents` KV, call POST `/api/llm` to convert each document to normalised Markdown, store the combined `ExtractionArtifact` (normalised text + confidence score) at `app:{id}:extraction` KV, update application status from "Queued" to "Extracting" then to "Scoring"
- [x] T035 [P] [US5] Create scoring worker in `server/workers/scoring.ts`: for each extracted application, execute N sequential LLM scoring passes (using the job's configured scoring run count), storing each `ScoringRun` at `app:{id}:runs` KV with run index, total score, per-category scores, must-have evaluation, evidence citations, improvement tips, AI model identifier, prompt version, and resource usage (inputTokens/outputTokens)
- [x] T036 [US5] Create aggregation worker in `server/workers/aggregation.ts`: once N runs exist, compute `AggregatedResult` using the job's configured strategy (median/mean/weighted), calculate variance across runs, determine decision (Eligible if above longlist threshold / Excluded if below / Needs Manual Review if variance exceeds threshold), store result at `app:{id}:result` KV, update application status to "Completed" or "Needs Manual Review"
- [x] T037 [US5] Create pipeline orchestrator in `server/services/pipeline.ts` that chains extraction → scoring → aggregation workers with application status transitions (Queued → Extracting → Scoring → Aggregating → Completed/Needs Manual Review/Failed), implements retry logic with exponential backoff (max 3 retries), and routes failures to the DLQ (`dlq` KV key) with error details
- [x] T038 [P] [US5] Create scoring data API routes in `server/routes/applications.ts`: GET `/api/applications/:applicationId/runs` (return all scoring runs from `app:{id}:runs`), GET `/api/applications/:applicationId/result` (return aggregated result from `app:{id}:result`), GET `/api/applications/:applicationId/extraction` (return extracted Markdown from `app:{id}:extraction`)
- [x] T039 [US5] Implement real API scoring functions in `src/lib/api-real.ts`: `getScoringRuns(applicationId)`, `getAggregatedResult(applicationId)`, `getExtractionArtifact(applicationId)` calling the routes from T038
- [x] T040 [P] [US5] Create DLQ management routes in `server/routes/dlq.ts`: GET `/api/dlq` (list all failed items from `dlq` KV with error details and retry counts), POST `/api/dlq/:itemId/retry` (remove item from DLQ, reset status, re-queue for processing via pipeline orchestrator)
- [x] T041 [US5] Create pipeline trigger endpoint in `server/routes/jobs.ts`: POST `/api/jobs/:jobId/process` that starts the pipeline orchestrator for all "Queued" applications in the job, or wire auto-trigger into the upload route from T030 to start processing after successful upload

**Checkpoint**: Complete AI scoring pipeline — applications are extracted, scored N times, aggregated with decisions, failures routed to DLQ

---

## Phase 9: User Story 6 — Recruiter views ranked lists and drills into application detail (Priority: P2)

**Goal**: Job detail view shows longlist, shortlist, and exclusions in separate tabs with sortable columns and filters. Clicking a candidate opens a full drill-down showing documents, extracted text, all N scoring runs, evidence, and final decision.

**Independent Test**: Navigate to a completed job → verify longlist/shortlist/exclusions tabs have correct counts → drill into one application → verify all scoring run details are shown.

### Implementation for User Story 6

- [x] T042 [P] [US6] Extend GET `/api/jobs/:jobId/applications` in `server/routes/applications.ts` to support query params: `list` (longlist|shortlist|excluded), `sortField` (score|name|date), `sortOrder` (asc|desc), `varianceMin` (number), `page` and `pageSize` for pagination — filter applications by comparing final score against the job's longlist/shortlist thresholds from the active config version
- [x] T043 [US6] Implement real API list and detail functions in `src/lib/api-real.ts`: `getApplications(jobId, { list, sortField, sortOrder, varianceMin, page, pageSize })`, `getApplicationDetail(applicationId)` (returns application with documents, extraction, scoring runs, aggregated result) using the extended routes from T042 and T038
- [x] T044 [US6] Update `src/components/JobDetailView.tsx` to display three tabs (Longlist, Shortlist, Exclusions) each fetching the filtered application list with correct counts displayed on the tab headers
- [x] T045 [US6] Update `src/components/ApplicationsTable.tsx` to support sortable column headers (score, candidate name, upload date), a variance threshold filter input, and virtual scrolling for rendering 1,000+ rows smoothly
- [x] T046 [US6] Update `src/components/ApplicationDetail.tsx` to show the full drill-down: original documents with download links, extracted text from extraction artifact, all N individual scoring runs with per-category score breakdown and evidence citations, and the aggregated final decision with consolidated rationale and improvement tips
- [x] T047 [US6] Add navigation link in `src/components/ApplicationDetail.tsx` that routes eligible and "Needs Manual Review" applications to the ManualReviewView component

**Checkpoint**: Ranked candidate lists with full drill-down are functional

---

## Phase 10: User Story 7 — Recruiter performs a manual review (Priority: P3)

**Goal**: Three-pane manual review interface (job specification left, rubric scoring form centre, application content right). Recruiter allocates points per category, adds comments, saves the review, and every change is recorded in an immutable audit trail.

**Independent Test**: Open manual review for a flagged application → allocate points in all categories → save → reload → verify saved scores and comments persist with audit trail entries.

### Implementation for User Story 7

- [x] T048 [P] [US7] Create manual review API routes in `server/routes/applications.ts`: GET `/api/applications/:applicationId/manual-review` (return saved `ManualReviewData` from `app:{id}:manual-review` KV including rubric scores, comment, audit trail), POST `/api/applications/:applicationId/manual-review` (save review data; for each changed rubric category, append a `ManualReviewAuditEntry` with reviewer username, timestamp, category name, previous value, and new value to the audit trail array; persist to KV)
- [x] T049 [US7] Implement real API manual review functions in `src/lib/api-real.ts`: `getManualReview(applicationId)`, `saveManualReview(applicationId, reviewData)` calling the routes from T048
- [x] T050 [US7] Update `src/components/ManualReviewView.tsx` to render three resizable, independently scrollable panes: job specification (left pane — displays the job's rubric categories and must-have criteria), rubric scoring form (centre pane — input fields for points per category and overall comment), application content (right pane — extracted text and original document viewer)
- [x] T051 [US7] Implement live scoring calculation in `src/components/ManualReviewView.tsx` that recalculates and displays the total weighted score immediately as the recruiter changes points in any rubric category
- [x] T052 [US7] Implement save and reload persistence in `src/components/ManualReviewView.tsx`: on page load, call `api.getManualReview()` and pre-populate all rubric score fields and comments with previously saved values; on save, call `api.saveManualReview()` with current state and display success confirmation
- [x] T053 [US7] Display the audit trail in `src/components/ManualReviewView.tsx` or a sub-component showing a chronological list of all `ManualReviewAuditEntry` records (reviewer, timestamp, category, old value → new value) for the current application

**Checkpoint**: Manual review with live scoring, persistence, and immutable audit trail is fully functional

---

## Phase 11: User Story 8 — Admin/recruiter monitors the processing pipeline (Priority: P3)

**Goal**: Dashboard shows system-wide stats (queued, processing, completed, failed), filterable job list, pipeline visualiser per job, and failure queue browser with retry controls. Stats auto-refresh within 30 seconds.

**Independent Test**: While jobs are processing, verify stats update without manual page refresh. Simulate a failure → verify it appears in the failure queue with a retry option.

### Implementation for User Story 8

- [x] T054 [P] [US8] Create stats API route in `server/routes/stats.ts`: GET `/api/stats` that aggregates counts across all jobs from KV (total queued, extracting, scoring, aggregating, completed, failed applications) and returns a `SystemStats` object; optionally cache in `system:stats` KV for performance
- [x] T055 [P] [US8] Create audit query route in `server/routes/audit.ts`: GET `/api/audit` with query params for `entityType`, `eventType`, `startDate`, `endDate`, `page`, and `pageSize` — reads from `ledger` KV and returns filtered, paginated `ProcessingEvent` entries
- [x] T056 [US8] Implement real API monitoring functions in `src/lib/api-real.ts`: `getSystemStats()`, `getDLQItems()`, `retryDLQItem(itemId)`, `getAuditEvents(filters)` calling the routes from T054, T055, and T040
- [x] T057 [US8] Update `src/components/DashboardView.tsx` to fetch real system stats via `api.getSystemStats()` and implement automatic 30-second polling refresh (using `setInterval` or TanStack Query's `refetchInterval`) with a visible "last updated" timestamp
- [x] T058 [US8] Update `src/components/DashboardView.tsx` to support filtering the job list by department and organisation using dropdown filters that pass query params to GET `/api/jobs`
- [x] T059 [US8] Update `src/components/PipelineVisualizer.tsx` to display real pipeline stage counts (Extraction → Scoring → Aggregation → Complete) with percentage completion bars calculated from actual application statuses for the selected job
- [x] T060 [US8] Create failure queue browser in `src/components/FailureQueueView.tsx` (or section within DashboardView) showing DLQ items with error details, retry count, timestamps, and a "Retry" button that calls `api.retryDLQItem(itemId)` and refreshes the list

**Checkpoint**: All Stack A user stories (US1–US8) are complete and independently testable

---

## Phase 12: Stack B — Solution Scaffold & Domain Layer (Milestone E1 + E2)

**Purpose**: Scaffold the .NET Clean Architecture solution structure and implement domain entities, value objects, repository interfaces, EF Core persistence, and MediatR application layer

- [x] T061 Create .NET 9 solution in `dotnet/` with projects: `Domain` (class library), `Application` (class library), `Infrastructure` (class library), `Web.Server` (ASP.NET Core), `Web.Client` (Blazor WASM), `Domain.Tests` (xUnit), `Application.Tests` (xUnit), `Infrastructure.Tests` (xUnit), `Web.Tests` (xUnit + bUnit) — configure project references per Clean Architecture dependency rules
- [x] T062 [P] Implement domain entities in `dotnet/src/Domain/Entities/`: `User.cs` (username, role, fullName, email, department, passwordHash, createdAt, lastLogin), `Job.cs` (jobCode, title, department, organisation, postingDate, status, currentConfigVersionId), `Application.cs` (jobId, status, documents, finalScore, finalDecision, variance), `ScoringRun.cs` (runIndex, totalScore, categoryScores, mustHaveEvaluation, evidenceCitations, improvementTips, aiModelId, promptVersion, resourceUsage), `AggregatedResult.cs` (finalScore, decision, variance, confidence, consolidatedRationale)
- [x] T063 [P] Implement domain value objects in `dotnet/src/Domain/ValueObjects/`: `JobConfigVersion.cs` (rubric categories, must-have criteria, scoringRunCount, aggregationStrategy, thresholds, varianceThreshold), `RubricCategory.cs` (name, weight, description), `ExtractionArtifact.cs` (normalisedText, confidenceScore, status), `MustHaveResult.cs`, `EvidenceCitation.cs`
- [x] T064 [P] Implement domain enums in `dotnet/src/Domain/Enums/`: `ApplicationStatus.cs` (Queued, Extracting, Scoring, Aggregating, Completed, NeedsManualReview, Failed), `UserRole.cs` (Admin, Recruiter), `JobStatus.cs`, `AggregationStrategy.cs` (Median, Mean, Weighted), `Decision.cs` (Eligible, Excluded, NeedsManualReview)
- [x] T065 [P] Implement domain events in `dotnet/src/Domain/Events/ProcessingEvent.cs` (actor, eventType, entityType, entityId, payload, timestamp, correlationId) and repository interfaces in `dotnet/src/Domain/Interfaces/`: `IJobRepository.cs`, `IApplicationRepository.cs`, `IUserRepository.cs` with async CRUD and query methods
- [x] T066 Configure EF Core DbContext in `dotnet/src/Infrastructure/Persistence/AppDbContext.cs` with entity type configurations in `dotnet/src/Infrastructure/Persistence/Configurations/` for all entities; configure SQLite provider for local development and Azure SQL for production
- [x] T067 Generate initial EF Core code-first migration in `dotnet/src/Infrastructure/Persistence/Migrations/` from the domain entity configurations
- [x] T068 [P] Configure MediatR and FluentValidation pipeline in `dotnet/src/Application/DependencyInjection.cs`: register `ValidationBehaviour.cs` and `LoggingBehaviour.cs` in `dotnet/src/Application/Common/Behaviours/`; configure assembly scanning for handlers and validators
- [x] T069 [P] Implement repository classes in `dotnet/src/Infrastructure/Persistence/Repositories/`: `JobRepository.cs`, `ApplicationRepository.cs`, `UserRepository.cs` implementing the domain interfaces using EF Core DbContext
- [x] T070 Implement MediatR handlers for auth and user use cases in `dotnet/src/Application/Users/`: `Commands/CreateUserCommand.cs`, `Commands/ResetPasswordCommand.cs`, `Commands/DeleteUserCommand.cs`, `Commands/ChangePasswordCommand.cs` and `Queries/GetUsersQuery.cs`, `Queries/GetCurrentUserQuery.cs` with FluentValidation validators
- [x] T071 [P] Implement MediatR handlers for job use cases in `dotnet/src/Application/Jobs/`: `Commands/CreateJobCommand.cs`, `Commands/UpdateJobConfigCommand.cs` and `Queries/GetJobsQuery.cs`, `Queries/GetJobDetailQuery.cs` with FluentValidation validators
- [x] T072 [P] Implement MediatR handlers for application use cases in `dotnet/src/Application/Applications/`: `Commands/UploadApplicationsCommand.cs`, `Commands/SaveManualReviewCommand.cs`, `Commands/RetryDlqItemCommand.cs` and `Queries/GetApplicationsQuery.cs`, `Queries/GetScoringRunsQuery.cs`, `Queries/GetAggregatedResultQuery.cs` with FluentValidation validators
- [x] T073 Implement `ICurrentUserService` interface in `dotnet/src/Application/Common/Interfaces/ICurrentUserService.cs` and its infrastructure implementation in `dotnet/src/Infrastructure/Services/CurrentUserService.cs` that reads the authenticated user from the HTTP context for RBAC enforcement in handlers

---

## Phase 13: Stack B — Web API Endpoints (Milestone E3)

**Purpose**: Implement ASP.NET Core minimal API endpoints matching Stack A's API contracts and wire to MediatR handlers

- [x] T074 Configure ASP.NET Core host in `dotnet/src/Web/Server/Program.cs`: register Entra ID authentication (Microsoft Identity), MediatR services, EF Core DbContext, CORS policy, and Swagger/OpenAPI generation
- [x] T074a [FR-019] Wire Blazor WASM hosting in `dotnet/src/Web.Server/`: add `Microsoft.AspNetCore.Components.WebAssembly.Server` NuGet package to `TalentMatch.Web.Server.csproj`, add `app.UseBlazorFrameworkFiles()` and `app.UseStaticFiles()` middleware before auth in `Program.cs`, and add `app.MapFallbackToFile("index.html")` after all API endpoint mappings to serve the Blazor client at the root URL
- [x] T074b [FR-020] Seed default admin user in `dotnet/src/Web.Server/Program.cs`: after `db.Database.Migrate()`, check if `Users` table is empty and insert admin user (username=`admin`, SHA-256 hash of `adm1n99`, role=`admin`, department=`all`) matching Stack A's `server/services/init-users.ts` behaviour
- [x] T074c [FR-021] Configure Blazor WASM cookie auth in `dotnet/src/Web.Client/Program.cs`: add `CookieHandler` (`DelegatingHandler`) that sets `BrowserRequestCredentials.Include` on every request, register `HttpClient` via `IHttpClientFactory` with the handler, add `Microsoft.Extensions.Http` NuGet package; in `dotnet/src/Web.Server/Program.cs` set cookie options (`HttpOnly=true`, `SameSite=Strict`, `SecurePolicy=SameAsRequest`) and update CORS to `AllowCredentials()` with `SetIsOriginAllowed`
- [x] T075 [P] Implement auth endpoints in `dotnet/src/Web/Server/Endpoints/AuthEndpoints.cs`: POST `/api/auth/login`, POST `/api/auth/logout`, GET `/api/auth/me`, POST `/api/auth/change-password` — each endpoint validates the request and sends the corresponding MediatR command/query
- [x] T076 [P] Implement user management endpoints in `dotnet/src/Web/Server/Endpoints/UsersEndpoints.cs`: GET `/api/users`, POST `/api/users`, DELETE `/api/users/:userId`, POST `/api/users/:userId/reset-password`, GET/POST/PUT `/api/users/reset-requests` — admin-only endpoints enforced via authorization policy
- [x] T077 [P] Implement job endpoints in `dotnet/src/Web/Server/Endpoints/JobsEndpoints.cs`: GET `/api/jobs` (department-filtered for recruiters), POST `/api/jobs`, GET `/api/jobs/:jobId`, PUT `/api/jobs/:jobId/config`, POST `/api/jobs/:jobId/process` — wired to MediatR handlers
- [x] T078 [P] Implement application endpoints in `dotnet/src/Web/Server/Endpoints/ApplicationsEndpoints.cs`: POST `/api/jobs/:jobId/applications/upload`, GET `/api/jobs/:jobId/applications`, GET `/api/applications/:applicationId`, GET runs/result/extraction/manual-review, POST manual-review — wired to MediatR handlers
- [x] T079 [P] Implement stats and DLQ endpoints in `dotnet/src/Web/Server/Endpoints/StatsEndpoints.cs` (GET `/api/stats`, GET `/api/audit`) and `dotnet/src/Web/Server/Endpoints/DlqEndpoints.cs` (GET `/api/dlq`, POST `/api/dlq/:itemId/retry`) — wired to MediatR handlers
- [x] T080 Add OpenAPI/Swagger generation and configuration in `dotnet/src/Web/Server/Program.cs` with XML documentation comments on all endpoints for integration documentation with the awr-platform backend

---

## Phase 14: Stack B — Blazor WASM Frontend (Milestone E4)

**Purpose**: Implement Blazor WebAssembly pages and components matching Stack A's UI for all user stories (US1–US8)

- [x] T081 Create typed API client service in `dotnet/src/Web/Client/Services/ApiClient.cs`: an `HttpClient`-based service (equivalent to `src/lib/api.ts`) with typed methods for all API endpoints (auth, users, jobs, applications, scoring, manual review, stats, DLQ, audit); register as scoped service in `dotnet/src/Web/Client/Program.cs`
- [x] T082 [P] Implement Login page in `dotnet/src/Web/Client/Pages/Login.razor` (US1): username and password form, error message display for invalid credentials, redirect to dashboard on successful login
- [x] T083 [P] Implement Dashboard page in `dotnet/src/Web/Client/Pages/Dashboard.razor` (US1, US8): department-filtered job list (admin sees all, recruiter sees own department), system-wide stats cards with 30-second auto-refresh timer, department/organisation filter dropdowns
- [x] T084 [P] Implement User Management component in `dotnet/src/Web/Client/Components/UserManagement.razor` (US2): user list table, create user form (username, role, department, password), delete confirmation dialog, password reset controls, password-reset request approval/rejection
- [x] T085 [P] Implement Job Creation component in `dotnet/src/Web/Client/Components/CreateJobDialog.razor` (US3): job details form (title, department, organisation, posting date), rubric upload with LLM extraction and weight validation, must-have criteria, scoring run count, aggregation strategy, threshold configuration
- [x] T086 [P] Implement Application Upload component in `dotnet/src/Web/Client/Components/UploadApplications.razor` (US4): drag-and-drop file zone, per-file progress bars, type/size validation with inline errors, duplicate fingerprint warning with link-or-separate option
- [x] T087 Implement Job Detail page in `dotnet/src/Web/Client/Pages/JobDetail.razor` (US6): three tabs (Longlist, Shortlist, Exclusions) with counts, sortable application table, variance filter, click-through to application detail showing documents, extracted text, all N scoring runs with evidence, and aggregated decision
- [x] T088 Implement Manual Review page in `dotnet/src/Web/Client/Pages/ManualReview.razor` (US7): three resizable panes (job spec, rubric scoring form, application content), live total score recalculation, save/load persistence, chronological audit trail display
- [x] T089 [P] Implement shared Blazor components in `dotnet/src/Web/Client/Components/`: `JobCard.razor` (organisation, days since posting), `ApplicationsTable.razor` (sortable columns, virtual scrolling), `PipelineVisualizer.razor` (stage counts and percentages), `StatusBadge.razor` (coloured status indicators)
- [x] T090 [P] Configure SignalR client in `dotnet/src/Web/Client/Services/SignalRService.cs` for real-time pipeline status updates and automatic dashboard stat refresh without polling
- [x] T091 Implement LLM proxy service in `dotnet/src/Infrastructure/Services/LlmProxyService.cs` for Azure OpenAI calls routed through APIM (server-side only; no client-side API keys)

---

## Phase 15: Polish & Cross-Cutting Concerns

**Purpose**: Testing infrastructure, performance optimisation, security hardening, and documentation

- [x] T092 [P] Configure Vitest testing framework in `vitest.config.ts` with `jsdom` environment for frontend component tests and Node environment for backend tests per Milestone D requirements; add test scripts to `package.json`
- [x] T093 [P] Create unit tests for auth operations in `tests/unit/auth.test.ts` covering SHA-256 hash generation, login with valid/invalid credentials, password change with correct/incorrect current password, and session management
- [x] T094 [P] Create integration tests for API routes in `tests/integration/api.test.ts` covering real API calls against local KV backend for jobs CRUD, application upload, user management, and scoring data retrieval
- [x] T095 [P] Create unit tests for KV storage operations in `tests/unit/local-kv.test.ts` covering all CRUD operations on `server/storage/local-kv.ts` (get, set, delete, keys, initialize)
- [x] T096 [P] Create xUnit tests for Stack B in `dotnet/src/Tests/`: domain entity invariant tests in `Domain.Tests/`, command handler happy-path tests in `Application.Tests/`, repository integration tests (EF Core SQLite) in `Infrastructure.Tests/`, API endpoint routing tests in `Web.Tests/`
- [x] T097 [P] Add loading states, skeleton screens, and toast notifications across all Stack A components (`src/components/`) per UI precision requirements; ensure all async operations show loading indicators
- [x] T098 Implement optimistic updates with rollback and a visible sync status indicator in `src/lib/api-real.ts` for network interruption resilience across all write operations
- [x] T099 [P] Validate RBAC enforcement end-to-end: verify all `server/routes/` handlers check department scoping for recruiters; verify recruiter accounts cannot access jobs, applications, or data outside their assigned department
- [x] T100 Final security hardening: audit all routes in `server/routes/` for authentication middleware enforcement, verify no client-side LLM API keys in `src/` code, ensure no SAS tokens are used (RBAC only for Blob Storage per constitution §IV)
- [x] T101 [P] Update `README.md` with developer setup instructions for both Stack A (Node.js/React) and Stack B (.NET/Blazor); document API_MODE configuration and environment variables
- [x] T102 Create `specs/001-talent-matching-platform/quickstart.md` with step-by-step developer onboarding guide: prerequisites, environment setup, running both stacks locally, creating test data, and verifying each user story independently

---

## Phase 16: Stack B — Blazor WASM Feature Completion

**Purpose**: Complete Blazor WebAssembly UI integration and feature parity with React frontend — Phase 13 tasks created component scaffolding but left critical gaps in navigation wiring, feature completeness, and missing pages/dialogs

**Audit Findings**: Blazor UI is ~40% feature-complete; many components exist but are orphaned from the UI. The React frontend (`src/components/`) serves as the reference implementation for all features below.

### Navigation & User Profile (FR-022, FR-023 — Unblocks all features)

- [x] T103 [US1/US2] Create UserMenu component in `dotnet/src/Web.Client/Components/UserMenu.razor`: display authenticated user name, role badge, and department in the layout header; provide logout button (calls `ApiClient.LogoutAsync()`), change password link (opens ChangePasswordDialog), and admin-only "Manage Users" link; wire into `MainLayout.razor` — reference: `src/components/UserMenu.tsx`
- [x] T104 [US1] Update NavMenu in `dotnet/src/Web.Client/Layout/NavMenu.razor`: show/hide links based on auth state (hide Login when authenticated, show Failure Queue link for admins); add conditional navigation items matching the React `App.tsx` routing structure

### User Management Completion (FR-024, FR-029)

- [x] T105 [US2] Create ChangePasswordDialog component in `dotnet/src/Web.Client/Components/ChangePasswordDialog.razor`: current password, new password, confirm password fields; call `ApiClient.ChangePasswordAsync()`; display success/error feedback; closeable dialog — reference: `src/components/ChangePasswordDialog.tsx`
- [x] T106 [US2] Wire UserManagement.razor as accessible page/dialog from UserMenu: add route (`/users`) or dialog trigger so admin users can reach user management from the UserMenu component created in T103
- [x] T107 [US2] Add password reset request handling to `dotnet/src/Web.Client/Components/UserManagement.razor`: add a "Reset Requests" tab displaying pending requests from `ApiClient.GetResetRequestsAsync()`; approve button (with new password input) and reject button calling `ApiClient.ResolveResetRequestAsync()` — reference: `src/components/UserManagementDialog.tsx`

### Job Creation Completion (FR-023, FR-028)

- [x] T108 [US3] Wire CreateJobDialog into Dashboard: add "Create Job" button to `Dashboard.razor` that opens `CreateJobDialog.razor` as a dialog/modal; close dialog and refresh job list on success
- [x] T109 [US3] Complete CreateJobDialog rubric management in `dotnet/src/Web.Client/Components/CreateJobDialog.razor`: add rubric category rows (add/remove with name, weight, description), weight inputs with sum-to-1.0 validation, must-have criteria list (add/remove), job specification file upload with LLM extraction via `ApiClient` — reference: `src/components/CreateJobDialog.tsx` and `src/components/UploadRubricDialog.tsx`

### Application Upload Completion (FR-023)

- [x] T110 [US4] Wire UploadApplications into JobDetail: add "Upload Applications" button to `JobDetail.razor` that opens `UploadApplications.razor` as a dialog; refresh application list on upload success
- [x] T111 [US4] Enhance UploadApplications component in `dotnet/src/Web.Client/Components/UploadApplications.razor`: add drag-and-drop file zone, per-file upload progress indicators, improved inline validation error display — reference: `src/components/UploadApplicationsDialog.tsx`

### Pipeline Visualization Integration (FR-023)

- [x] T112 [US5/US8] Integrate PipelineVisualizer into JobDetail: render `PipelineVisualizer.razor` on `JobDetail.razor` with real pipeline stage counts from the API; update counts on auto-refresh — reference: `src/components/PipelineVisualizer.tsx` within `src/components/JobDetailView.tsx`

### Application Detail Page (FR-025)

- [x] T113 [US6] Create ApplicationDetail page in `dotnet/src/Web.Client/Pages/ApplicationDetail.razor`: display original documents with download links, extracted text from extraction artifact, all N individual scoring runs with per-category score breakdown and evidence citations, aggregated final decision with consolidated rationale and improvement tips, and a "Manual Review" navigation link for eligible/flagged applications — reference: `src/components/ApplicationDetail.tsx`

### Manual Review Completion (FR-026)

- [x] T114 [US7] Complete ManualReview per-category scoring in `dotnet/src/Web.Client/Pages/ManualReview.razor`: replace placeholder left pane with actual rubric categories and must-have criteria from the job configuration; add per-category point allocation input fields in the centre pane; implement live weighted score recalculation; display chronological audit trail entries (reviewer, timestamp, category, old → new value) — reference: `src/components/ManualReviewView.tsx`

### Failure Queue View (FR-027)

- [x] T115 [US8] Create FailureQueue page in `dotnet/src/Web.Client/Pages/FailureQueue.razor`: display DLQ items with error details, failure reason, retry count, timestamps; "Retry" button calling `ApiClient.RetryDlqItemAsync()`; 30-second auto-refresh; add route `/failure-queue` and NavMenu link — reference: `src/components/FailureQueueView.tsx`

### Bugfix: User Creation Error Handling & Login (FR-030, FR-031)

- [x] T116 [US2] Fix `CreateUser()` in `dotnet/src/Web.Client/Components/UserManagement.razor`: check the boolean return value of `Api.CreateUserAsync()`; only close the form and clear fields on success; show a visible error message (e.g. `errorMessage` string rendered in red) on failure; add a success message (e.g. "User created successfully") that auto-clears after display — currently the return value is ignored and the form closes silently even when creation fails
- [x] T117 [US2] Update `ApiClient.CreateUserAsync` in `dotnet/src/Web.Client/Services/ApiClient.cs`: change return type from `bool` to a result type or throw `HttpRequestException` on non-success status codes so the component can display the server error message (e.g. "Username already exists"); read the response body error message for 4xx/5xx responses and surface it to the caller
- [x] T118 [US2] Add error handling to all other mutating methods in `UserManagement.razor` (`ConfirmDeleteUser`, `ConfirmResetPassword`, `ApproveRequest`, `RejectRequest`): check return values, display error/success feedback, and only update local state on success — apply the same pattern from T116
- [x] T119 [US2] Add integration test in `dotnet/tests/Web.Tests/UserManagementIntegrationTests.cs`: test the full create user → verify user appears in GET /api/users response → login as new user → verify GET /api/auth/me returns the new user's data; use `WebApplicationFactory<Program>` with in-memory SQLite to test against real endpoints
- [x] T120 [US2] Add unit test in `dotnet/tests/Application.Tests/PasswordHashConsistencyTests.cs`: verify that the SHA-256 hash produced by `CreateUserCommandHandler.HashPassword()` for a known password exactly matches the hash computed by the login comparison in `AuthEndpoints` (i.e. `Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(password)))`) — ensures create and login use identical hashing
- [x] T121 [US2] Add bUnit component test in `dotnet/tests/Web.Tests/UserManagementTests.cs`: render `UserManagement.razor` with a mocked `ApiClient`; simulate creating a user where `CreateUserAsync` returns `false`; verify the error message is displayed and the form remains open; simulate creating a user where `CreateUserAsync` returns `true`; verify the user list is refreshed and the form closes with a success message

**Checkpoint**: All Blazor WASM feature gaps addressed — UI feature parity with React frontend for US1–US8

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion — **BLOCKS all user stories**
- **US1 (Phase 3)**: Depends on Foundational — first MVP story
- **US2 (Phase 4)**: Depends on Foundational — can run in **parallel with US1** (different routes and components)
- **US3 (Phase 5)**: Depends on Foundational — can run in **parallel with US1 and US2**
- **US3a (Phase 6)**: Depends on **US3** (job with approved rubric must exist); **BLOCKS US5** (production scoring requires approved prompt)
- **US4 (Phase 7)**: Depends on **US3** (jobs must exist before uploading applications)
- **US5 (Phase 8)**: Depends on **US3a** (production-approved prompt required) AND **US4** (applications must be uploaded)
- **US6 (Phase 9)**: Depends on **US5** (scored applications needed for ranked lists)
- **US7 (Phase 10)**: Depends on **US6** (application detail view provides navigation to manual review); also serves **US3a** (test case review uses same manual review interface)
- **US8 (Phase 11)**: Depends on **US5** (pipeline monitoring requires real pipeline data); can run in **parallel with US6 and US7**
- **Stack B Foundation (Phase 12)**: Can start after Phase 2 — **independent of Stack A user stories**
- **Stack B Web API (Phase 13)**: Depends on Phase 12
- **Stack B Blazor (Phase 14)**: Depends on Phase 13
- **Polish (Phase 15)**: Depends on all desired user stories being complete
- **Blazor Feature Completion (Phase 16)**: Depends on Phase 14 (scaffolded components must exist)

### User Story Dependencies

- **US1 (P1)**: Can start after Foundational — No dependencies on other stories
- **US2 (P1)**: Can start after Foundational — Independent of US1 (different routes and components)
- **US3 (P2)**: Can start after Foundational — Independent of US1/US2
- **US3a (P1)**: Depends on **US3** (needs job with approved rubric); **BLOCKS US5** (pipeline cannot score without approved prompt)
- **US4 (P2)**: Depends on **US3** (needs jobs to exist to upload applications against); can run in **parallel with US3a**
- **US5 (P2)**: Depends on **US3a** (approved prompt required) AND **US4** (uploaded applications required)
- **US6 (P2)**: Depends on **US5** (needs scored/aggregated applications for ranked lists)
- **US7 (P3)**: Depends on **US6** (manual review is accessed from the application detail drill-down)
- **US8 (P3)**: Depends on **US5** (needs real pipeline data); can run in parallel with US6/US7

### Within Each User Story

- Server routes created before API client functions
- API client functions implemented before component updates
- Core implementation before cross-component integration
- Story validated as complete before moving to next dependent story

### Parallel Opportunities

**Stack A stories that can start simultaneously (after Foundational):**
- US1, US2, and US3 have zero inter-dependencies — all three can begin in parallel

**After US3 completes, US3a and US4 can run in parallel:**
- US3a (prompt management) and US4 (application upload) have no mutual dependency
- Both depend only on US3 (job creation)

**Stack A stories that can run in parallel (later phases):**
- US7 and US8 can start in parallel once US5/US6 are complete (no mutual dependency)

**Stack B is fully independent of Stack A user stories:**
- Phases 12–14 (Stack B) can run concurrently with Phases 3–11 (Stack A)
- Both stacks share spec.md acceptance scenarios as the cross-stack contract
- US3a Stack B tasks (T150–T173) can run in parallel with Stack A US3a tasks (T132–T149) since they target different codebases

**Within each story, tasks marked [P] can execute simultaneously:**
- Server routes on different files (e.g., T017 and T018 in US2)
- Domain entities, value objects, and enums in Stack B (T062, T063, T064)
- US3a setup tasks T132 and T133 can run in parallel
- US3a Stack B domain tasks T150, T151, T152, T153 can all run in parallel

---

## Parallel Example: User Story 1

```text
# These tasks target different files — launch in parallel:
Task T011: "Create auth API routes in server/routes/auth.ts"
Task T012: "Create default admin user seeding in server/services/init-users.ts"

# Then sequentially (each depends on the prior):
Task T013: "Implement real API auth functions in src/lib/api-real.ts" (needs T011)
Task T014: "Update LoginForm.tsx" (needs T013)
Task T015: "Update App.tsx session management" (needs T013)
Task T016: "Implement department-filtered dashboard" (needs T015)
```

## Parallel Example: US1 + US2 + US3 (after Foundational)

```text
# Three developers can work on these simultaneously:
Developer A — US1: T011-T016 (auth routes + login + dashboard)
Developer B — US2: T017-T022 (user management routes + dialogs)
Developer C — US3: T023-T028, T122-T131 (job routes + create job dialog)

# No shared files or blocking dependencies between these three stories
```

## Parallel Example: US3a + US4 (after US3)

```text
# After US3 completes, these two stories can start in parallel:
Developer A — US3a: T132-T149 (Stack A prompt management, then T150-T173 for Stack B)
Developer B — US4:  T029-T033 (application upload routes + UI)

# US3a and US4 share no files and have no mutual dependency
# Both depend only on US3 (job creation) being complete
```

## Parallel Example: US3a Stack A + Stack B

```text
# Within US3a, Stack A and Stack B tasks target completely different codebases:
Developer A — Stack A: T132-T149 (types, server routes, API client, React UI)
Developer B — Stack B: T150-T173 (domain entities, EF Core, MediatR, Blazor UI)

# Both developers work independently — shared spec.md acceptance scenarios as contract
```

## Parallel Example: Stack A + Stack B (full project)

```text
# After Foundational phase (Phase 2) completes:
Team A: Stack A user stories (Phases 3–11)
Team B: Stack B scaffold and implementation (Phases 12–14, 16)

# Both teams work independently using shared spec.md as contract
# Both stacks implement the same API endpoint contracts from contracts/
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL — blocks all stories)
3. Complete Phase 3: User Story 1
4. **STOP and VALIDATE**: Login with admin → see all jobs; login as recruiter → see only department-scoped jobs
5. Deploy/demo if ready — minimal viable product is functional

### Incremental Delivery

1. Setup + Foundational → Foundation ready
2. Add US1 (Auth + Dashboard) → Test independently → Deploy/Demo **(MVP!)**
3. Add US2 (User Management) → Test independently → Demo admin workflow
4. Add US3 (Job Creation) → Test independently → Demo job setup with rubric
5. Add US3a (Prompt Management) → Test independently → Demo prompt lifecycle & approval gate
6. Add US4 (Bulk Upload) → Test independently → Demo document ingestion
7. Add US5 (AI Pipeline) → Test independently → Demo core value proposition
8. Add US6 (Ranked Lists) → Test independently → Demo recruiter decision view
9. Add US7 (Manual Review) → Test independently → Demo defensibility workflow
10. Add US8 (Pipeline Monitoring) → Test independently → Full operational dashboard
11. Each story adds value without breaking previous stories

### Parallel Team Strategy

With multiple developers:

1. Team completes Setup + Foundational together
2. Once Foundational is done:
   - Developer A: US1 (Auth) → US3a (Prompts) → US5 (Pipeline)
   - Developer B: US2 (Users) → US3 (Jobs) → US4 (Upload)
   - Developer C: Stack B scaffold (Phases 12–14, 16)
3. After US5/US6 complete:
   - Developer A: US7 (Manual Review)
   - Developer B: US8 (Monitoring)
4. All: Polish (Phase 15)

---

## Notes

- [P] tasks = different files, no dependencies — safe for parallel execution
- [Story] label maps each task to a specific user story for traceability
- Stack A (React/Express) is the primary implementation — the working prototype
- Stack B (.NET/Blazor) is a parallel track targeting a specific customer requirement
- Both stacks share spec.md acceptance scenarios as cross-stack contracts
- Both stacks implement API contracts defined in `contracts/api-stack-a.md` and `contracts/api-stack-b.md`
- Existing components (LoginForm, DashboardView, etc.) are UPDATED, not recreated from scratch
- US3a (Prompt Management) is the primary new feature — tasks T132–T173 cover both stacks
- US3a is a hard prerequisite for US5 (scoring pipeline) — production scoring is gated on prompt approval
- `src/lib/auth.ts` client-side auth logic is replaced by `server/routes/auth.ts` server-side implementation
- `src/lib/api.ts` mock functions are preserved in `src/lib/api-mock.ts`; real API mode is added alongside
- The `API_MODE` environment variable controls whether the app uses mock data or real KV-backed persistence
- Commit after each task or logical group
- Stop at any checkpoint to validate the story independently
- Avoid: vague tasks, editing the same file in parallel, cross-story dependencies that break story independence