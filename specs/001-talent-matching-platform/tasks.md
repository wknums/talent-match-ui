# Tasks: Talent Matching Platform

**Input**: Design documents from `/specs/001-talent-matching-platform/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md (empty), data-model.md (not created), contracts/ (not created)

**Tests**: Not explicitly requested in the feature specification. Testing infrastructure and minimum-coverage tests are included as part of the Polish phase per Milestone D of the plan.

**Organization**: Tasks are grouped by user story (US1–US8) to enable independent implementation and testing. Stack B (Blazor/.NET Clean Architecture) tasks are organized by implementation milestone (E1–E4) as a separate parallel track.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
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

**Goal**: Recruiter fills in job details (title, department, organisation, posting date), uploads or specifies a scoring rubric with weighted categories, sets must-have criteria, and configures scoring runs and aggregation strategy.

**Independent Test**: Create a job end-to-end → job card appears on dashboard with correct title, department, organisation, and days since posting.

### Implementation for User Story 3

- [x] T023 [P] [US3] Create job CRUD API routes in `server/routes/jobs.ts`: GET `/api/jobs` (list all jobs from `jobs` KV, filtered by requester's department for recruiters), POST `/api/jobs` (create job with title, department, organisation, posting date, status, and initial config version; append to `jobs` KV and create `job:{jobId}:versions`), GET `/api/jobs/:jobId` (return job with computed stats — application counts by status)
- [x] T024 [P] [US3] Create job configuration versioning route in `server/routes/jobs.ts`: PUT `/api/jobs/:jobId/config` (create a new `JobConfigVersion` with rubric categories, must-have criteria, scoring run count, aggregation strategy, longlist/shortlist thresholds, and variance threshold; append to `job:{jobId}:versions` without affecting in-progress scoring on previous versions)
- [x] T025 [US3] Implement real API job functions in `src/lib/api-real.ts`: `getJobs()`, `getJob(jobId)`, `createJob(jobData)`, `updateJobConfig(jobId, configData)` calling the server routes from T023–T024
- [x] T026 [US3] Update `src/components/CreateJobDialog.tsx` to use the `api` client for job creation; submit all fields (title, department, organisation, posting date) plus initial configuration (rubric, must-haves, scoring runs, aggregation strategy, thresholds); refresh dashboard on success
- [x] T027 [US3] Update `src/components/UploadRubricDialog.tsx` to extract rubric categories via POST `/api/llm` proxy, validate that weights sum to 1.0, present categories for user confirmation, and save via `api.updateJobConfig()` on confirmation
- [x] T028 [US3] Update `src/components/JobCard.tsx` to display the organisation name alongside the department and calculate/display days since posting from the `postingDate` field

**Checkpoint**: Jobs can be created with full rubric and scoring configuration; job cards display on dashboard

---

## Phase 6: User Story 4 — Recruiter uploads applications in bulk (Priority: P2)

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

## Phase 7: User Story 5 — AI scoring pipeline runs and results are visible (Priority: P2)

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

## Phase 8: User Story 6 — Recruiter views ranked lists and drills into application detail (Priority: P2)

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

## Phase 9: User Story 7 — Recruiter performs a manual review (Priority: P3)

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

## Phase 10: User Story 8 — Admin/recruiter monitors the processing pipeline (Priority: P3)

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

## Phase 11: Stack B — Solution Scaffold & Domain Layer (Milestone E1 + E2)

**Purpose**: Scaffold the .NET Clean Architecture solution structure and implement domain entities, value objects, repository interfaces, EF Core persistence, and MediatR application layer

- [ ] T061 Create .NET 9 solution in `dotnet/` with projects: `Domain` (class library), `Application` (class library), `Infrastructure` (class library), `Web.Server` (ASP.NET Core), `Web.Client` (Blazor WASM), `Domain.Tests` (xUnit), `Application.Tests` (xUnit), `Infrastructure.Tests` (xUnit), `Web.Tests` (xUnit + bUnit) — configure project references per Clean Architecture dependency rules
- [ ] T062 [P] Implement domain entities in `dotnet/src/Domain/Entities/`: `User.cs` (username, role, fullName, email, department, passwordHash, createdAt, lastLogin), `Job.cs` (jobCode, title, department, organisation, postingDate, status, currentConfigVersionId), `Application.cs` (jobId, status, documents, finalScore, finalDecision, variance), `ScoringRun.cs` (runIndex, totalScore, categoryScores, mustHaveEvaluation, evidenceCitations, improvementTips, aiModelId, promptVersion, resourceUsage), `AggregatedResult.cs` (finalScore, decision, variance, confidence, consolidatedRationale)
- [ ] T063 [P] Implement domain value objects in `dotnet/src/Domain/ValueObjects/`: `JobConfigVersion.cs` (rubric categories, must-have criteria, scoringRunCount, aggregationStrategy, thresholds, varianceThreshold), `RubricCategory.cs` (name, weight, description), `ExtractionArtifact.cs` (normalisedText, confidenceScore, status), `MustHaveResult.cs`, `EvidenceCitation.cs`
- [ ] T064 [P] Implement domain enums in `dotnet/src/Domain/Enums/`: `ApplicationStatus.cs` (Queued, Extracting, Scoring, Aggregating, Completed, NeedsManualReview, Failed), `UserRole.cs` (Admin, Recruiter), `JobStatus.cs`, `AggregationStrategy.cs` (Median, Mean, Weighted), `Decision.cs` (Eligible, Excluded, NeedsManualReview)
- [ ] T065 [P] Implement domain events in `dotnet/src/Domain/Events/ProcessingEvent.cs` (actor, eventType, entityType, entityId, payload, timestamp, correlationId) and repository interfaces in `dotnet/src/Domain/Interfaces/`: `IJobRepository.cs`, `IApplicationRepository.cs`, `IUserRepository.cs` with async CRUD and query methods
- [ ] T066 Configure EF Core DbContext in `dotnet/src/Infrastructure/Persistence/AppDbContext.cs` with entity type configurations in `dotnet/src/Infrastructure/Persistence/Configurations/` for all entities; configure SQLite provider for local development and Azure SQL for production
- [ ] T067 Generate initial EF Core code-first migration in `dotnet/src/Infrastructure/Persistence/Migrations/` from the domain entity configurations
- [ ] T068 [P] Configure MediatR and FluentValidation pipeline in `dotnet/src/Application/DependencyInjection.cs`: register `ValidationBehaviour.cs` and `LoggingBehaviour.cs` in `dotnet/src/Application/Common/Behaviours/`; configure assembly scanning for handlers and validators
- [ ] T069 [P] Implement repository classes in `dotnet/src/Infrastructure/Persistence/Repositories/`: `JobRepository.cs`, `ApplicationRepository.cs`, `UserRepository.cs` implementing the domain interfaces using EF Core DbContext
- [ ] T070 Implement MediatR handlers for auth and user use cases in `dotnet/src/Application/Users/`: `Commands/CreateUserCommand.cs`, `Commands/ResetPasswordCommand.cs`, `Commands/DeleteUserCommand.cs`, `Commands/ChangePasswordCommand.cs` and `Queries/GetUsersQuery.cs`, `Queries/GetCurrentUserQuery.cs` with FluentValidation validators
- [ ] T071 [P] Implement MediatR handlers for job use cases in `dotnet/src/Application/Jobs/`: `Commands/CreateJobCommand.cs`, `Commands/UpdateJobConfigCommand.cs` and `Queries/GetJobsQuery.cs`, `Queries/GetJobDetailQuery.cs` with FluentValidation validators
- [ ] T072 [P] Implement MediatR handlers for application use cases in `dotnet/src/Application/Applications/`: `Commands/UploadApplicationsCommand.cs`, `Commands/SaveManualReviewCommand.cs`, `Commands/RetryDlqItemCommand.cs` and `Queries/GetApplicationsQuery.cs`, `Queries/GetScoringRunsQuery.cs`, `Queries/GetAggregatedResultQuery.cs` with FluentValidation validators
- [ ] T073 Implement `ICurrentUserService` interface in `dotnet/src/Application/Common/Interfaces/ICurrentUserService.cs` and its infrastructure implementation in `dotnet/src/Infrastructure/Services/CurrentUserService.cs` that reads the authenticated user from the HTTP context for RBAC enforcement in handlers

---

## Phase 12: Stack B — Web API Endpoints (Milestone E3)

**Purpose**: Implement ASP.NET Core minimal API endpoints matching Stack A's API contracts and wire to MediatR handlers

- [ ] T074 Configure ASP.NET Core host in `dotnet/src/Web/Server/Program.cs`: register Entra ID authentication (Microsoft Identity), MediatR services, EF Core DbContext, CORS policy, and Swagger/OpenAPI generation
- [ ] T075 [P] Implement auth endpoints in `dotnet/src/Web/Server/Endpoints/AuthEndpoints.cs`: POST `/api/auth/login`, POST `/api/auth/logout`, GET `/api/auth/me`, POST `/api/auth/change-password` — each endpoint validates the request and sends the corresponding MediatR command/query
- [ ] T076 [P] Implement user management endpoints in `dotnet/src/Web/Server/Endpoints/UsersEndpoints.cs`: GET `/api/users`, POST `/api/users`, DELETE `/api/users/:userId`, POST `/api/users/:userId/reset-password`, GET/POST/PUT `/api/users/reset-requests` — admin-only endpoints enforced via authorization policy
- [ ] T077 [P] Implement job endpoints in `dotnet/src/Web/Server/Endpoints/JobsEndpoints.cs`: GET `/api/jobs` (department-filtered for recruiters), POST `/api/jobs`, GET `/api/jobs/:jobId`, PUT `/api/jobs/:jobId/config`, POST `/api/jobs/:jobId/process` — wired to MediatR handlers
- [ ] T078 [P] Implement application endpoints in `dotnet/src/Web/Server/Endpoints/ApplicationsEndpoints.cs`: POST `/api/jobs/:jobId/applications/upload`, GET `/api/jobs/:jobId/applications`, GET `/api/applications/:applicationId`, GET runs/result/extraction/manual-review, POST manual-review — wired to MediatR handlers
- [ ] T079 [P] Implement stats and DLQ endpoints in `dotnet/src/Web/Server/Endpoints/StatsEndpoints.cs` (GET `/api/stats`, GET `/api/audit`) and `dotnet/src/Web/Server/Endpoints/DlqEndpoints.cs` (GET `/api/dlq`, POST `/api/dlq/:itemId/retry`) — wired to MediatR handlers
- [ ] T080 Add OpenAPI/Swagger generation and configuration in `dotnet/src/Web/Server/Program.cs` with XML documentation comments on all endpoints for integration documentation with the awr-platform backend

---

## Phase 13: Stack B — Blazor WASM Frontend (Milestone E4)

**Purpose**: Implement Blazor WebAssembly pages and components matching Stack A's UI for all user stories (US1–US8)

- [ ] T081 Create typed API client service in `dotnet/src/Web/Client/Services/ApiClient.cs`: an `HttpClient`-based service (equivalent to `src/lib/api.ts`) with typed methods for all API endpoints (auth, users, jobs, applications, scoring, manual review, stats, DLQ, audit); register as scoped service in `dotnet/src/Web/Client/Program.cs`
- [ ] T082 [P] Implement Login page in `dotnet/src/Web/Client/Pages/Login.razor` (US1): username and password form, error message display for invalid credentials, redirect to dashboard on successful login
- [ ] T083 [P] Implement Dashboard page in `dotnet/src/Web/Client/Pages/Dashboard.razor` (US1, US8): department-filtered job list (admin sees all, recruiter sees own department), system-wide stats cards with 30-second auto-refresh timer, department/organisation filter dropdowns
- [ ] T084 [P] Implement User Management component in `dotnet/src/Web/Client/Components/UserManagement.razor` (US2): user list table, create user form (username, role, department, password), delete confirmation dialog, password reset controls, password-reset request approval/rejection
- [ ] T085 [P] Implement Job Creation component in `dotnet/src/Web/Client/Components/CreateJobDialog.razor` (US3): job details form (title, department, organisation, posting date), rubric upload with LLM extraction and weight validation, must-have criteria, scoring run count, aggregation strategy, threshold configuration
- [ ] T086 [P] Implement Application Upload component in `dotnet/src/Web/Client/Components/UploadApplications.razor` (US4): drag-and-drop file zone, per-file progress bars, type/size validation with inline errors, duplicate fingerprint warning with link-or-separate option
- [ ] T087 Implement Job Detail page in `dotnet/src/Web/Client/Pages/JobDetail.razor` (US6): three tabs (Longlist, Shortlist, Exclusions) with counts, sortable application table, variance filter, click-through to application detail showing documents, extracted text, all N scoring runs with evidence, and aggregated decision
- [ ] T088 Implement Manual Review page in `dotnet/src/Web/Client/Pages/ManualReview.razor` (US7): three resizable panes (job spec, rubric scoring form, application content), live total score recalculation, save/load persistence, chronological audit trail display
- [ ] T089 [P] Implement shared Blazor components in `dotnet/src/Web/Client/Components/`: `JobCard.razor` (organisation, days since posting), `ApplicationsTable.razor` (sortable columns, virtual scrolling), `PipelineVisualizer.razor` (stage counts and percentages), `StatusBadge.razor` (coloured status indicators)
- [ ] T090 [P] Configure SignalR client in `dotnet/src/Web/Client/Services/SignalRService.cs` for real-time pipeline status updates and automatic dashboard stat refresh without polling
- [ ] T091 Implement LLM proxy service in `dotnet/src/Infrastructure/Services/LlmProxyService.cs` for Azure OpenAI calls routed through APIM (server-side only; no client-side API keys)

---

## Phase 14: Polish & Cross-Cutting Concerns

**Purpose**: Testing infrastructure, performance optimisation, security hardening, and documentation

- [ ] T092 [P] Configure Vitest testing framework in `vitest.config.ts` with `jsdom` environment for frontend component tests and Node environment for backend tests per Milestone D requirements; add test scripts to `package.json`
- [ ] T093 [P] Create unit tests for auth operations in `tests/unit/auth.test.ts` covering SHA-256 hash generation, login with valid/invalid credentials, password change with correct/incorrect current password, and session management
- [ ] T094 [P] Create integration tests for API routes in `tests/integration/api.test.ts` covering real API calls against local KV backend for jobs CRUD, application upload, user management, and scoring data retrieval
- [ ] T095 [P] Create unit tests for KV storage operations in `tests/unit/local-kv.test.ts` covering all CRUD operations on `server/storage/local-kv.ts` (get, set, delete, keys, initialize)
- [ ] T096 [P] Create xUnit tests for Stack B in `dotnet/src/Tests/`: domain entity invariant tests in `Domain.Tests/`, command handler happy-path tests in `Application.Tests/`, repository integration tests (EF Core SQLite) in `Infrastructure.Tests/`, API endpoint routing tests in `Web.Tests/`
- [ ] T097 [P] Add loading states, skeleton screens, and toast notifications across all Stack A components (`src/components/`) per UI precision requirements; ensure all async operations show loading indicators
- [ ] T098 Implement optimistic updates with rollback and a visible sync status indicator in `src/lib/api-real.ts` for network interruption resilience across all write operations
- [ ] T099 [P] Validate RBAC enforcement end-to-end: verify all `server/routes/` handlers check department scoping for recruiters; verify recruiter accounts cannot access jobs, applications, or data outside their assigned department
- [ ] T100 Final security hardening: audit all routes in `server/routes/` for authentication middleware enforcement, verify no client-side LLM API keys in `src/` code, ensure no SAS tokens are used (RBAC only for Blob Storage per constitution §IV)
- [ ] T101 [P] Update `README.md` with developer setup instructions for both Stack A (Node.js/React) and Stack B (.NET/Blazor); document API_MODE configuration and environment variables
- [ ] T102 Create `specs/001-talent-matching-platform/quickstart.md` with step-by-step developer onboarding guide: prerequisites, environment setup, running both stacks locally, creating test data, and verifying each user story independently

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion — **BLOCKS all user stories**
- **US1 (Phase 3)**: Depends on Foundational — first MVP story
- **US2 (Phase 4)**: Depends on Foundational — can run in **parallel with US1** (different routes and components)
- **US3 (Phase 5)**: Depends on Foundational — can run in **parallel with US1 and US2**
- **US4 (Phase 6)**: Depends on **US3** (jobs must exist before uploading applications)
- **US5 (Phase 7)**: Depends on **US4** (applications must be uploaded before pipeline runs)
- **US6 (Phase 8)**: Depends on **US5** (scored applications needed for ranked lists)
- **US7 (Phase 9)**: Depends on **US6** (application detail view provides navigation to manual review)
- **US8 (Phase 10)**: Depends on **US5** (pipeline monitoring requires real pipeline data); can run in **parallel with US6 and US7**
- **Stack B Foundation (Phase 11)**: Can start after Phase 2 — **independent of Stack A user stories**
- **Stack B Web API (Phase 12)**: Depends on Phase 11
- **Stack B Blazor (Phase 13)**: Depends on Phase 12
- **Polish (Phase 14)**: Depends on all desired user stories being complete

### User Story Dependencies

- **US1 (P1)**: Can start after Foundational — No dependencies on other stories
- **US2 (P1)**: Can start after Foundational — Independent of US1 (different routes and components)
- **US3 (P2)**: Can start after Foundational — Independent of US1/US2
- **US4 (P2)**: Depends on **US3** (needs jobs to exist to upload applications against)
- **US5 (P2)**: Depends on **US4** (needs uploaded applications to process through pipeline)
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

**Stack A stories that can run in parallel (later phases):**
- US7 and US8 can start in parallel once US5/US6 are complete (no mutual dependency)

**Stack B is fully independent of Stack A user stories:**
- Phases 11–13 (Stack B) can run concurrently with Phases 3–10 (Stack A)
- Both stacks share spec.md acceptance scenarios as the cross-stack contract

**Within each story, tasks marked [P] can execute simultaneously:**
- Server routes on different files (e.g., T017 and T018 in US2)
- Domain entities, value objects, and enums in Stack B (T062, T063, T064)

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
Developer C — US3: T023-T028 (job routes + create job dialog)

# No shared files or blocking dependencies between these three stories
```

## Parallel Example: Stack A + Stack B

```text
# After Foundational phase (Phase 2) completes:
Team A: Stack A user stories (Phases 3–10)
Team B: Stack B scaffold and implementation (Phases 11–13)

# Both teams work independently using shared spec.md as contract
# Both stacks implement the same API endpoint contracts from plan.md
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
5. Add US4 (Bulk Upload) → Test independently → Demo document ingestion
6. Add US5 (AI Pipeline) → Test independently → Demo core value proposition
7. Add US6 (Ranked Lists) → Test independently → Demo recruiter decision view
8. Add US7 (Manual Review) → Test independently → Demo defensibility workflow
9. Add US8 (Pipeline Monitoring) → Test independently → Full operational dashboard
10. Each story adds value without breaking previous stories

### Parallel Team Strategy

With multiple developers:

1. Team completes Setup + Foundational together
2. Once Foundational is done:
   - Developer A: US1 (Auth) → US4 (Upload) → US5 (Pipeline)
   - Developer B: US2 (Users) → US3 (Jobs) → US6 (Lists)
   - Developer C: Stack B scaffold (Phases 11–13)
3. After US5/US6 complete:
   - Developer A: US7 (Manual Review)
   - Developer B: US8 (Monitoring)
4. All: Polish (Phase 14)

---

## Notes

- [P] tasks = different files, no dependencies — safe for parallel execution
- [Story] label maps each task to a specific user story for traceability
- Stack A (React/Express) is the primary implementation — the working prototype
- Stack B (.NET/Blazor) is a parallel track targeting a specific customer requirement
- Both stacks share spec.md acceptance scenarios as cross-stack contracts
- Existing components (LoginForm, DashboardView, etc.) are UPDATED, not recreated from scratch
- `src/lib/auth.ts` client-side auth logic is replaced by `server/routes/auth.ts` server-side implementation
- `src/lib/api.ts` mock functions are preserved in `src/lib/api-mock.ts`; real API mode is added alongside
- The `API_MODE` environment variable controls whether the app uses mock data or real KV-backed persistence
- Commit after each task or logical group
- Stop at any checkpoint to validate the story independently
- Avoid: vague tasks, editing the same file in parallel, cross-story dependencies that break story independence