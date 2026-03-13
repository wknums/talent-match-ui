# Tasks: US3 — Recruiter Creates a Job with Rubric and Scoring Config

**Input**: Design documents from `/specs/001-talent-matching-platform/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅ (api-stack-a.md, api-stack-b.md)

**Tests**: Not explicitly requested in the feature specification. No test tasks included.

**Organization**: Tasks are organized for User Story 3 (US3) only, covering both Stack A (React/Express) and Stack B (.NET Blazor). Tasks are sequenced by layer: types → server routes → API client → UI components, with Stack B following the same pattern within its architecture.

**Scope**: This task list covers US3 acceptance scenarios 1–6 from spec.md. US3a (prompt management) is a separate planning unit with its own tasks.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[US3]**: All tasks belong to User Story 3
- Include exact file paths in descriptions

## Path Conventions

- **Stack A**: `server/` (Express backend) + `src/` (React frontend) at repository root
- **Stack B**: `dotnet/src/` (Clean Architecture .NET solution) — separate directory to coexist with Stack A

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization, configuration, and shared utilities — completed in prior work

- [x] T001 Create API mode configuration by adding `API_MODE=mock|real` environment variable to `.env` and reading it in `server/index.ts` so the frontend can query which mode is active
- [x] T002 [P] Create KV key constants and pattern helpers in `server/storage/kv-keys.ts` defining all key patterns from the plan (`auth:users`, `auth:current-user`, `auth:reset-requests`, `jobs`, `job:{jobId}:versions`, `job:{jobId}:applications`, `app:{applicationId}:documents`, `app:{applicationId}:extraction`, `app:{applicationId}:runs`, `app:{applicationId}:result`, `app:{applicationId}:manual-review`, `dlq`, `ledger`, `system:stats`)
- [x] T003 [P] Create typed KV data helper utilities in `server/storage/kv-helpers.ts` wrapping StorageProvider with `getArray<T>`, `setArray<T>`, `pushToArray<T>`, `removeFromArray<T>`, `getOrDefault<T>` for safe typed access to KV collections
- [x] T004 [P] Create request validation middleware in `server/middleware/validate.ts` using Zod schemas for body, params, and query validation with structured JSON error responses

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented — completed in prior work

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T005 Create server-side session management and auth verification middleware in `server/middleware/auth.ts` that reads session from KV (`auth:current-user`), attaches the authenticated user to the Express request object, and returns 401 for unauthenticated requests
- [x] T006 [P] Create role-based access control middleware in `server/middleware/rbac.ts` exporting `requireRole('admin')` and `requireRole('recruiter')` Express middleware guards that check the user attached by the auth middleware
- [x] T007 [P] Create centralized error handling middleware in `server/middleware/error-handler.ts` that catches all route errors and returns structured JSON responses (`{ error, message, statusCode }`) with server-side error logging
- [x] T008 [P] Create audit ledger service in `server/services/audit.ts` exporting `appendEvent(actor, eventType, entityType, entityId, payload, correlationId)` that appends a `ProcessingEvent` entry to the `ledger` KV key with a generated timestamp and correlation ID
- [x] T009 Register all new route modules and middleware in `server/index.ts`: mount auth, users, jobs, applications, stats, audit, and DLQ routers under `/api/`; apply auth middleware globally (except login); apply error handler last
- [x] T010 Refactor `src/lib/api.ts` to create an API client factory: extract existing mock functions into `src/lib/api-mock.ts`, create a new `src/lib/api-real.ts` module for real API calls, and export a single `api` object from `src/lib/api.ts` that delegates to mock or real implementation based on the `API_MODE` environment variable

**Checkpoint**: Foundation ready — user story implementation can now begin in parallel

---

## Phase 3: User Story 3 — Core Job Creation (Priority: P2) 🎯 MVP

**Goal**: Recruiter fills in job details (title, department, organisation, posting date), optionally
uploads a job specification document and/or a separate rubric document for AI-based extraction,
reviews or manually defines a scoring rubric with weighted categories, sets must-have criteria and
desired criteria, and configures scoring runs and aggregation strategy. Document extraction uses the
external API at `AWR_SEQ_API_ENDPOINT/assess/passthrough`. Supported document types: pdf, jpg, md,
txt, docx.

**Independent Test**: Create a job end-to-end → job card appears on dashboard with correct title,
department, organisation, and days since posting. Upload a job spec → verify fields auto-populated
including desired criteria. Upload a rubric doc with mismatched title → verify warning is shown.

### Stack A Types

- [x] T011 [P] [US3] Add `DesiredCriteria` interface to `src/types/index.ts` (with `id`, `qualification`, `description` fields) and add `desiredCriteria` array field to `JobConfigVersion` type; add optional `jobDescription` field to `Job` type

### Stack A Server Routes

- [x] T012 [P] [US3] Create job CRUD API routes in `server/routes/jobs.ts`: GET `/api/jobs` (list all jobs from `jobs` KV, filtered by requester's department for recruiters), POST `/api/jobs` (create job with title, department, organisation, posting date, status, and initial config version; append to `jobs` KV and create `job:{jobId}:versions`), GET `/api/jobs/:jobId` (return job with computed stats — application counts by status)
- [x] T013 [P] [US3] Create job configuration versioning route in `server/routes/jobs.ts`: PUT `/api/jobs/:jobId/config` (create a new `JobConfigVersion` with rubric categories, must-have criteria, desired criteria, scoring run count, aggregation strategy, longlist/shortlist thresholds, and variance threshold; append to `job:{jobId}:versions` without affecting in-progress scoring on previous versions)
- [x] T014 [P] [US3] Update `server/routes/jobs.ts` POST `/api/jobs` and PUT `/api/jobs/:jobId/config` routes to accept and persist `desiredCriteria` and `jobDescription` fields alongside existing must-haves
- [x] T015 [P] [US3] Add `POST /api/jobs/extract-spec` route in `server/routes/jobs.ts` that accepts a multipart file upload (pdf, jpg, md, txt, docx), forwards the document to the external API at `AWR_SEQ_API_ENDPOINT/assess/passthrough` for extraction using the locally stored `EXTRACT_SPEC_SYSTEM_PROMPT` (FR-042), and returns extracted job metadata (title, description, department, organisation, must-haves, desired criteria, and optionally rubric categories with weights). During extraction a loading indicator MUST be shown in the UI and any extraction errors MUST be surfaced with a descriptive message (FR-044)
- [x] T016 [P] [US3] Add `POST /api/jobs/extract-rubric` route in `server/routes/jobs.ts` that accepts a multipart rubric document upload (pdf, jpg, md, txt, docx), forwards to `AWR_SEQ_API_ENDPOINT/assess/passthrough` using the locally stored `EXTRACT_RUBRIC_SYSTEM_PROMPT`, and returns extracted job title and rubric categories with weights summing to 1.0

### Stack A API Client

- [x] T017 [US3] Implement real API job functions in `src/lib/api-real.ts`: `getJobs()`, `getJob(jobId)`, `createJob(jobData)`, `updateJobConfig(jobId, configData)` calling the server routes from T012–T013
- [x] T018 [P] [US3] Update `src/lib/api-real.ts` and `src/lib/api-mock.ts` to add `extractJobSpec(file)` and `extractRubric(file)` functions calling the new extraction endpoints; update `createJob()` and `updateJobConfig()` to include `desiredCriteria` and `jobDescription` fields

### Stack A UI Components

- [x] T019 [US3] Update `src/components/CreateJobDialog.tsx` to use the `api` client for job creation; submit all fields (title, department, organisation, posting date) plus initial configuration (rubric, must-haves, scoring runs, aggregation strategy, thresholds); refresh dashboard on success
- [x] T020 [US3] Update `src/components/CreateJobDialog.tsx`: expand accepted file types to include `.jpg`, `.jpeg`, `.txt`; replace internal `llm()` calls with `POST /api/jobs/extract-spec` API calls; add desired criteria section (add/remove items with qualification and description); add separate rubric document upload button calling `POST /api/jobs/extract-rubric`; implement job title mismatch warning for rubric documents; implement auto-generation of draft rubric when no rubric found in spec and no separate rubric uploaded (60% weight to must-haves); include `desiredCriteria` and `jobDescription` in job submission payload
- [x] T021 [US3] Update `src/components/UploadRubricDialog.tsx`: expand accepted file types to include `.jpg`, `.jpeg`, `.txt`; replace `llm()` calls with `POST /api/jobs/extract-rubric` API call; add `jobTitle` prop for mismatch detection; display warning when extracted rubric job title differs from provided job title advising user to correct and re-upload
- [x] T022 [US3] Update `src/components/JobCard.tsx` to display the organisation name alongside the department and calculate/display days since posting from the `postingDate` field

### Stack B Domain & Infrastructure

- [x] T023 [P] [US3] Update `.NET` `JobConfigVersion` entity in `dotnet/src/Domain/Entities/JobConfigVersion.cs` to add `DesiredCriteriaJson` string property; update `Job` entity to add optional `JobDescription` property; generate EF Core migration
- [x] T024 [US3] Update `.NET` `CreateJobCommand`, `CreateJobDto`, `CreateJobRequest`, `UpdateConfigDto`, `UpdateJobConfigRequest` and `JobsEndpoints.cs` to accept and persist `desiredCriteria` and `jobDescription` fields

### Stack B Blazor Client

- [x] T025 [US3] Update `dotnet/src/Web.Client/Components/CreateJobDialog.razor`: add file upload capability for job specification documents (pdf, jpg, md, txt, docx) with `AWR_SEQ_API_ENDPOINT` extraction via backend proxy; add separate rubric document upload with job title mismatch warning; add desired criteria section; implement auto-generated draft rubric when no rubric found

**Checkpoint**: Core job creation is functional — jobs can be created with full rubric and scoring configuration including document upload with extraction via AWR_SEQ_API_ENDPOINT, desired criteria, and rubric mismatch warnings; job cards display on dashboard

---

## Phase 4: User Story 3 — Rubric Approval Status and Source Tracking (Priority: P2)

**Goal**: AI-generated or AI-extracted rubrics receive a `draft` approval status that the user must
explicitly toggle to `approved` before the rubric can be used for downstream prompt generation
(US3a). Manually defined rubrics default to `approved`. The rubric source (`manual`, `extracted`,
`generated`) is tracked for audit and to determine the default approval status. The raw LLM
extraction response is stored for auditability. This is a hard gate for prompt management (FR-033).

**Independent Test**: Create a job by uploading a job spec → verify rubric shows amber "Draft" badge
→ click "Approve Rubric" → verify green "Approved" badge. Create a job manually → verify rubric
defaults to "Approved". Verify raw extraction response is stored when AI generates rubric.

### Stack A Types

- [x] T026 [P] [US3] Add `RubricApprovalStatus` type (`'draft' | 'approved'`) and `RubricSource` type (`'manual' | 'extracted' | 'generated'`) to `src/types/index.ts`; add `rubricApprovalStatus: RubricApprovalStatus`, `rubricSource: RubricSource`, and `rawExtractionResponse?: string` fields to `JobConfigVersion` interface

### Stack A Server Routes

- [x] T027 [US3] Update `server/routes/jobs.ts` POST `/api/jobs` to accept `rubricSource` and `rawExtractionResponse` in the request body; set `rubricApprovalStatus` on the initial `JobConfigVersion` based on `rubricSource` — `'approved'` if `rubricSource === 'manual'`, `'draft'` if `rubricSource` is `'extracted'` or `'generated'`; include `rubricApprovalStatus`, `rubricSource`, and `rawExtractionResponse` in the response
- [x] T028 [US3] Update `server/routes/jobs.ts` PUT `/api/jobs/:jobId/config` to accept `rubricSource` and `rawExtractionResponse`; set `rubricApprovalStatus` on the new config version based on `rubricSource` using the same defaulting logic as T027; if rubric categories are changed via re-extraction, `rubricSource` should be `'extracted'` or `'generated'` and `rubricApprovalStatus` defaults to `'draft'`
- [x] T029 [US3] Add PUT `/api/jobs/:jobId/rubric-approval` route in `server/routes/jobs.ts` per contracts/api-stack-a.md: accept `{ "status": "approved" }`, validate job exists, validate current config version has `rubricApprovalStatus === 'draft'`, update `rubricApprovalStatus` to `'approved'` in-place (NOT a new config version per research.md R5), create `ProcessingEvent` audit entry with action `rubric.approved` via audit service, return `{ versionId, rubricApprovalStatus: 'approved', updatedAt }`; return 400 if rubric is already approved, 404 if job not found
- [x] T030 [US3] Update `server/routes/jobs.ts` GET `/api/jobs/:jobId` to include `rubricApprovalStatus`, `rubricSource`, and `rawExtractionResponse` fields on the `currentVersion` object in the response

### Stack A API Client

- [x] T031 [P] [US3] Add `updateRubricApproval(jobId: string, status: 'approved')` function to `src/lib/api-real.ts` calling PUT `/api/jobs/:jobId/rubric-approval`; update `createJob()` to include `rubricSource` and `rawExtractionResponse` in the request payload; update `updateJobConfig()` to include `rubricSource` and `rawExtractionResponse`
- [x] T032 [P] [US3] Add `updateRubricApproval(jobId: string, status: 'approved')` function to `src/lib/api-mock.ts` that updates the job's current config version `rubricApprovalStatus` to `'approved'` in mock storage; update `createJob()` to set `rubricApprovalStatus` based on `rubricSource`; update mock job generation to include sample `rubricApprovalStatus` and `rubricSource` values
- [x] T033 [US3] Update `src/lib/api.ts` API interface to export the `updateRubricApproval` function signature so both mock and real implementations are accessible through the unified API client

### Stack A UI Components

- [x] T034 [US3] Update `src/components/CreateJobDialog.tsx` to track rubric provenance and approval: add `rubricSource` state that is set to `'extracted'` or `'generated'` when AI extraction populates the rubric and `'manual'` when the user manually enters rubric categories; add `rubricApprovalStatus` state defaulting per `rubricSource`; display an amber "Draft" badge next to the rubric section header when status is `'draft'` and a green "Approved" badge when `'approved'`; add "Approve Rubric" button (visible only when status is `'draft'`) that calls `api.updateRubricApproval(jobId, 'approved')` if job is saved or toggles local state for unsaved jobs; include `rubricSource`, `rubricApprovalStatus`, and `rawExtractionResponse` (captured from extraction API response) in the job submission payload
- [x] T035 [US3] Update `src/components/CreateJobDialog.tsx` to store the raw LLM extraction response: when `POST /api/jobs/extract-spec` or `POST /api/jobs/extract-rubric` returns a result, capture the full raw response JSON in a `rawExtractionResponse` state variable and include it in the job creation or config update payload for audit purposes (FR-042)

### Stack B Domain Layer

- [x] T036 [P] [US3] Create `RubricApprovalStatus` enum in `dotnet/src/Domain/Enums/RubricApprovalStatus.cs` with values `Draft` and `Approved`; create `RubricSource` enum in `dotnet/src/Domain/Enums/RubricSource.cs` with values `Manual`, `Extracted`, and `Generated`
- [x] T037 [P] [US3] Update `JobConfigVersion` entity in `dotnet/src/Domain/Entities/JobConfigVersion.cs` to add: `RubricApprovalStatus` string property (default `"draft"`), `RubricSource` string property (default `"manual"`), and `RawExtractionResponse` nullable string property — per data-model.md field definitions

### Stack B Infrastructure

- [x] T038 [US3] Generate EF Core migration `AddRubricApprovalStatus` in `dotnet/src/Infrastructure/Persistence/Migrations/`: add column `JobConfigVersions.RubricApprovalStatus` (nvarchar, default `'draft'`), add column `JobConfigVersions.RubricSource` (nvarchar, default `'manual'`), add column `JobConfigVersions.RawExtractionResponse` (nvarchar(max), nullable)

### Stack B Application Layer

- [x] T039 [US3] Create `UpdateRubricApprovalCommand` in `dotnet/src/Application/Jobs/Commands/UpdateRubricApprovalCommand.cs`: accept `JobId` and `Status` (`"approved"` or `"draft"`); handler loads job, loads current `JobConfigVersion`, validates status is a valid value and differs from current, sets new status, saves changes, creates `ProcessingEvent` audit entry with action `rubric.approved` or `rubric.reverted-to-draft`, returns `RubricApprovalResult` record with `VersionId`, `RubricApprovalStatus`, and `UpdatedAt`; supports bidirectional toggle
- [x] T040 [US3] Update `CreateJobCommand` handler in `dotnet/src/Application/Jobs/Commands/CreateJobCommand.cs` to accept `RubricSource` and `RawExtractionResponse` parameters; set `RubricApprovalStatus` to `"draft"` regardless of source — all rubrics require explicit approval; persist `RubricSource` and `RawExtractionResponse` on the new `JobConfigVersion`
- [x] T041 [US3] Update `UpdateJobConfigCommand` handler in `dotnet/src/Application/Jobs/Commands/UpdateJobConfigCommand.cs` to accept `RubricSource` and `RawExtractionResponse`; always set `RubricApprovalStatus` to `"draft"` on new config versions — user must re-approve after any config change

### Stack B Web API Endpoints

- [x] T042 [US3] Add PUT `/api/jobs/{jobId}/rubric-approval` endpoint to `dotnet/src/Web.Server/Endpoints/JobsEndpoints.cs` per contracts/api-stack-b.md: map to `UpdateRubricApprovalCommand` via MediatR; accept `{ status: "approved" }` or `{ status: "draft" }` request body; return `RubricApprovalResult` with 200 OK; bidirectional toggle; add `[Authorize]` policy
- [x] T043 [US3] Update `CreateJobRequest` and `UpdateJobConfigRequest` records in `dotnet/src/Web.Server/Endpoints/JobsEndpoints.cs` (or their DTO locations) to include `RubricSource` and `RawExtractionResponse` fields; update endpoint mappings to pass these values through to the MediatR commands

### Stack B Blazor Client

- [x] T044 [P] [US3] Add `UpdateRubricApprovalAsync(string jobId, string status)` method to `dotnet/src/Web.Client/Services/ApiClient.cs` per contracts/api-stack-b.md: call PUT `/api/jobs/{jobId}/rubric-approval` with `{ status }` payload; return `RubricApprovalResult` response; update `CreateJobAsync()` and `UpdateJobConfigAsync()` to include `rubricSource` and `rawExtractionResponse` parameters
- [x] T045 [US3] Update `dotnet/src/Web.Client/Components/CreateJobDialog.razor` to add rubric approval tracking: add `rubricApprovalStatus` and `rubricSource` state fields; all rubrics default to `"draft"` with amber badge; add "Approve Rubric" button visible when status is `"draft"` and "Revert to Draft" button when `"approved"`; buttons call `ApiClient.UpdateRubricApprovalAsync()` (if job saved) or toggle local state; include `rubricSource`, `rubricApprovalStatus`, and `rawExtractionResponse` in create job request payload

**Checkpoint**: Rubric approval status is fully implemented in both stacks — all rubrics default to "Draft" status regardless of source, recruiters can toggle between Draft and Approved, raw extraction responses are stored for audit, and approval status gates downstream prompt management (US3a)

---

## Phase 5: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect US3 across both stacks

- [x] T046 [P] [US3] Verify rubric title mismatch warning message in `src/components/CreateJobDialog.tsx` and `src/components/UploadRubricDialog.tsx` matches spec language exactly: "advise the user to correct and upload the corrected rubric document and inform them that the existing rubric document will be discarded" per research.md R6
- [x] T047 [P] [US3] Verify rubric title mismatch warning in `dotnet/src/Web.Client/Components/CreateJobDialog.razor` matches spec language and React reference implementation
- [ ] T048 [US3] Run quickstart.md validation for US3: execute all verification steps from quickstart.md sections 5–6 (manual rubric entry → Approved badge, document upload → Draft badge → Approve button → Approved badge, rubric document upload with title mismatch → warning shown)

**Checkpoint**: US3 is fully validated across both stacks — all acceptance scenarios 1–6 pass

---

## Phase 6: User Story 5 — LLM-Backed Scoring Worker (FR-045/046/047) (Priority: P2) 🎯

**Goal**: Replace synthetic/random score generation with real LLM scoring via `AWR_SEQ_API_ENDPOINT/assess/passthrough`. Both stacks resolve `{{JOB_SPEC_TEXT}}` and `{{CANDIDATE_CV_TEXT}}` placeholders in the production-approved scoring prompt, send the resolved prompt + candidate CV text as multipart FormData, parse the structured JSON response, and map it to `ScoringRun` fields. The prompt test-run workflow (FR-046) uses the identical real scoring path — no mock/synthetic mode.

**Independent Test**: Create a job with rubric → approve rubric → generate + approve scoring prompt → upload application → trigger pipeline → verify scoring runs contain real LLM-generated scores (not random), evidence citations with actual CV quotes, and meaningful rationale text. Verify test runs also produce real LLM scores.

**Prerequisites**: Phases 1–4 complete; production-approved prompt exists for the job (US3a); application uploaded with successful extraction (US4); `AWR_SEQ_API_ENDPOINT` environment variable configured.

### Stack A — Scoring Worker Rewrite

- [x] T049 [US5] Rewrite `server/workers/scoring.ts` to replace `Math.random()` scoring with real LLM passthrough calls per contracts/scoring-passthrough.md: for each scoring run (1..`runCount`), load the `ScoringPrompt` by `promptVersionId` from `promptsKey(jobId)` via KV helpers, load `ExtractionArtifact` from `appExtractionKey(applicationId)` via `storage.get()`, load `Job` from `JOBS` KV for `jobDescription`, resolve placeholders in `prompt.promptText` (`{{JOB_SPEC_TEXT}}` → `job.jobDescription`, `{{CANDIDATE_CV_TEXT}}` → `extraction.markdown`), build `FormData` with `promptFile` (resolved prompt as `text/plain`, filename `score-prompt.md`) and `specFile` (CV markdown as `text/plain`, filename `candidate-cv.md`), POST to `${AWR_SEQ_API_ENDPOINT}/assess/passthrough`, parse response text as JSON, map to `ScoringRun` fields per data-model.md field mapping table (`composite_score` → `overallScore`, `rubric_scores` → `subScores` + `evidenceCitations`, `eligibility_gate` → `mustHaveResult`, `notes` → `rationale`, `improvement_recommendations` → `improvementRecommendations`); on JSON parse failure create `ScoringRun` with `status: 'Failed'` and raw response in `rationale`; on HTTP error throw to let `withRetry()` in pipeline.ts handle retries; measure `durationMs` per run; set `modelDeploymentId` to `'passthrough-llm'`; validate `AWR_SEQ_API_ENDPOINT` env var exists and throw descriptive error if missing

### Stack B — Service Interface Extension

- [x] T050 [P] [US5] Add `ScoreAsync(string resolvedPrompt, string candidateText, CancellationToken cancellationToken = default)` method signature to `ILlmProxyService` interface in `dotnet/src/Application/Common/Interfaces/ILlmProxyService.cs` — returns `Task<string>` (raw LLM response text); this method is distinct from `SendPromptAsync` because FormData field construction differs (scoring sends resolved prompt as `promptFile` and candidate text as `specFile`, unlike `SendPromptAsync` which combines system+user into `promptFile`)

- [x] T051 [US5] Implement `ScoreAsync` in `dotnet/src/Infrastructure/Services/LlmProxyService.cs` per contracts/scoring-passthrough.md calling convention: build `MultipartFormDataContent` with `promptFile` (resolved prompt as `text/plain`, filename `score-prompt.md`) and `specFile` (candidate text as `text/plain`, filename `candidate-cv.md`), POST to `{AWR_SEQ_API_ENDPOINT}/assess/passthrough`, throw `InvalidOperationException` on non-success status codes with error details, return raw response text on success; validate `AWR_SEQ_API_ENDPOINT` is configured

### Stack B — Application Command

- [x] T052 [US5] Create `ScoreApplicationCommand` in `dotnet/src/Application/Scoring/Commands/ScoreApplicationCommand.cs`: accept `ApplicationId`, `JobId`, `RunCount`, and `PromptVersionId`; inject `ILlmProxyService`, `IApplicationRepository`, `IJobRepository`, `IScoringPromptRepository`; handler loads `ScoringPrompt` by `PromptVersionId` from repo, loads `ExtractionArtifact` via `_applicationRepo.GetExtractionAsync(applicationId)`, loads `Job` via `_jobRepo.GetByIdAsync(jobId)` for `JobDescription`, resolves placeholders in `prompt.PromptText` (`{{JOB_SPEC_TEXT}}` → `job.JobDescription`, `{{CANDIDATE_CV_TEXT}}` → `extraction.NormalisedText`); for each run `i` in `1..RunCount`: calls `_llmService.ScoreAsync(resolvedPrompt, candidateText)`, parses response JSON using `System.Text.Json`, maps to `ScoringRun` entity per data-model.md field mapping table (`composite_score` → `TotalScore`, `rubric_scores` → `CategoryScoresJson`, `eligibility_gate` → `MustHaveEvaluationJson`, `rubric_scores` mapped → `EvidenceCitationsJson`, `improvement_recommendations` → `ImprovementTipsJson`), sets `AiModelId` to `"passthrough-llm"`, sets `PromptVersion` to prompt ID, persists via `_applicationRepo.AddScoringRunAsync(run)`; on JSON parse failure: create `ScoringRun` with `TotalScore = 0` and error details serialized in `MustHaveEvaluationJson`; on API error: throw to allow upstream retry logic; return `IReadOnlyList<ScoringRun>` of all runs created

### Stack B — Endpoint Wiring

- [x] T053 [US5] Wire the `POST /api/jobs/{jobId}/process` endpoint in `dotnet/src/Web.Server/Endpoints/JobsEndpoints.cs` to invoke the full scoring pipeline: after the existing production-approved prompt gate check, load all applications for the job via `GetApplicationsQuery`, for each application with status `Queued` or `Scored` — send `ScoreApplicationCommand` via MediatR with `applicationId`, `jobId`, `runCount` from job config, and the production-approved `promptVersionId`; update application status to `Scoring` before calling and `Completed` after; wrap in try/catch per application so one failure doesn't block others; return `Results.Accepted()` with count of applications queued for processing

**Checkpoint**: Scoring runs now contain real LLM-generated scores — `overallScore` values are meaningful (not uniformly distributed), `evidenceCitations[].snippet` contains actual CV text, `rationale` is a substantive paragraph. Both production runs and prompt test runs (FR-046) use the same passthrough path.

---

## Phase 7: US5 Scoring — Polish & Validation

**Purpose**: End-to-end validation that scoring is real, test runs share the path, and synthetic scores are gone

- [ ] T054 [P] [US5] Run quickstart.md scoring validation for Stack A: execute all verification steps from quickstart.md "End-to-End Scoring Test" section — create job → approve rubric → generate prompt → activate → approve for production → upload application → trigger process → GET `/api/applications/:appId/runs` → verify each run has `overallScore` from LLM (not random), `subScores` matching rubric categories, `mustHaveResult` with real evaluation, `evidenceCitations` with actual CV quotes, `rationale` with substantive text, `status: 'Success'`
- [ ] T055 [P] [US5] Run quickstart.md scoring validation for Stack B: same verification against Stack B API at `https://localhost:5001/api/` — verify scoring runs contain real LLM output and that the `/api/jobs/{jobId}/process` endpoint invokes `ScoreApplicationCommand`
- [ ] T056 [US5] Verify prompt test-run workflow (FR-046) uses real scoring in both stacks: create a prompt test run (POST test-run with example CVs), trigger processing, verify test-case scoring runs contain real LLM scores (not synthetic), confirm test results are excluded from production ranked lists but use the identical passthrough scoring path
- [ ] T057 [US5] Verify error handling: test with `AWR_SEQ_API_ENDPOINT` unset → confirm descriptive error and application moves to DLQ; test with malformed LLM response → confirm `ScoringRun` created with `status: 'Failed'` and raw response stored in `rationale` (Stack A) / `MustHaveEvaluationJson` (Stack B)

**Checkpoint**: FR-045, FR-046, FR-047 are fully validated — all scoring is LLM-backed, test runs share the same path, synthetic scores are eliminated

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately ✅ COMPLETE
- **Foundational (Phase 2)**: Depends on Setup completion — **BLOCKS all user stories** ✅ COMPLETE
- **US3 Core (Phase 3)**: Depends on Foundational — core job creation ✅ COMPLETE
- **US3 Rubric Approval (Phase 4)**: Depends on Phase 3 — rubric approval workflow ⬜ PENDING
- **US3 Polish (Phase 5)**: Depends on Phase 4 — US3 final validation ⬜ PENDING
- **US5 Scoring Worker (Phase 6)**: Depends on US3a (approved prompt) + US4 (uploaded applications); can start in parallel with Phase 4/5 since it modifies different files ⬜ PENDING
- **US5 Scoring Polish (Phase 7)**: Depends on Phase 6 — scoring validation ⬜ PENDING

### User Story Dependencies

- **US3 (P2)**: Can start after Foundational — Independent of US1/US2
- **US3a (P1)**: Depends on **US3** (job with **approved** rubric must exist); blocked until Phase 4 completes
- **US4 (P2)**: Depends on **US3** (jobs must exist before uploading applications)
- **US5 (P2)**: Depends on **US3a** (production-approved prompt required) AND **US4** (uploaded applications)
- **FR-045/046/047 (this task list)**: Implementation can proceed once US3a and US4 exist in codebase; end-to-end testing requires a running `AWR_SEQ_API_ENDPOINT`

### Within Phase 4 (Rubric Approval)

- Types (T026) MUST complete before server routes and API client tasks
- Server routes (T027–T030) before API client (T031–T033)
- API client (T031–T033) before UI components (T034–T035)
- Stack B domain (T036–T037) before infrastructure (T038)
- Stack B infrastructure (T038) before application layer (T039–T041)
- Stack B application layer (T039–T041) before web API (T042–T043)
- Stack B web API (T042–T043) before Blazor client (T044–T045)
- Stack A and Stack B task chains are independent — can run in parallel

### Within Phase 6 (Scoring Worker)

- **Stack A**: T049 is a single-file rewrite — no internal dependencies
- **Stack B**: T050 (interface) → T051 (implementation) → T052 (command) → T053 (endpoint) — strict sequential chain
- **Stack A and Stack B are fully independent** — can run in parallel
- Phase 7 validation tasks (T054–T057) depend on Phase 6 completion but are independent of each other

### Parallel Opportunities

**Phase 6 — Stack A vs Stack B (fully independent):**
- Stack A: T049 (single task, one file)
- Stack B: T050 → T051 → T052 → T053 (sequential chain, 4 files)
- Both chains can execute simultaneously

**Phase 7 — Validation (partially parallel):**
- T054 + T055 can run in parallel (Stack A vs Stack B validation)
- T056 depends on both stacks working
- T057 can run in parallel with T054/T055

---

## Parallel Example: Phase 6 (Scoring Worker)

```text
# Two developers can work on Stack A and Stack B simultaneously:

Developer A — Stack A:
  T049 (rewrite server/workers/scoring.ts)

Developer B — Stack B:
  T050 (ILlmProxyService) → T051 (LlmProxyService) → T052 (ScoreApplicationCommand) → T053 (JobsEndpoints)

# No shared files between stacks
```

## Parallel Example: US3 Phase 4

```text
# Two developers can work on Stack A and Stack B simultaneously:

Developer A — Stack A:
  T026 → T027 → T028 → T029 → T030 → T031 + T032 (parallel) → T033 → T034 → T035

Developer B — Stack B:
  T036 + T037 (parallel) → T038 → T039 → T040 → T041 → T042 → T043 → T044 → T045

# No shared files or blocking dependencies between Stack A and Stack B
```

---

## Implementation Strategy

### MVP First — Scoring Worker (Phase 6)

1. Phases 1–3 already complete ✅
2. Phase 4 (Rubric Approval) may proceed in parallel — different files
3. **Phase 6** is the core deliverable for FR-045/046/047:
   - Stack A T049: Single file rewrite — highest impact, fastest path to real scoring
   - Stack B T050–T053: Four-file chain following Clean Architecture
4. **STOP and VALIDATE** (Phase 7): End-to-end scoring with real LLM calls

### Incremental Delivery

1. T049: Stack A scoring worker rewrite — immediate value, replaces synthetic scores
2. T050–T051: Stack B service layer — builds foundation for scoring command
3. T052: Stack B command handler — core scoring logic in application layer
4. T053: Stack B endpoint wiring — connects API trigger to scoring pipeline
5. T054–T057: Validation — confirm real scoring across both stacks

### Suggested Execution Order (Single Developer)

1. Start with Stack A (T049) — one file, high impact, validates passthrough contract
2. Stack B interface (T050) — fast, extends existing interface
3. Stack B implementation (T051) — implements the HTTP call
4. Stack B command (T052) — core application logic
5. Stack B endpoint (T053) — wires everything together
6. Validation (T054 → T055 → T056 → T057)

---

## Notes

- [P] tasks = different files, no dependencies — can run in parallel
- [US3] / [US5] labels for story traceability
- FR-046 (test runs use real scoring) requires no separate implementation — it's automatically satisfied by making `runScoring()` use real LLM calls, since test runs and production runs share the same scoring function
- The scoring prompt's `{{JOB_SPEC_TEXT}}` and `{{CANDIDATE_CV_TEXT}}` placeholders are resolved at scoring time — not stored pre-resolved — because each candidate has different CV text
- Stack A uses `extraction.markdown` for CV text; Stack B uses `extraction.NormalisedText` — different property names, same content
- `AWR_SEQ_API_ENDPOINT` must be set to a running AWReason API instance for scoring to work; absence should produce a clear error
- Commit after each task or logical group
- Stop at any checkpoint to validate independently