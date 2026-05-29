# Cross-Stack Parity Checklist — 001 Talent Matching Platform

**Feature**: 001 — Talent Matching Platform  
**Spec**: [spec.md](../spec.md)  
**Created**: March 11, 2026  
**Last Verified**: April 24, 2026  
**Overall Status**: ⚠️ Minor structural differences remain (61/62)

---

## Summary

| Section | Items | Checked | Status |
|---------|-------|---------|--------|
| US1 — Authentication & Dashboard | 7 | 7/7 | ✅ |
| US2 — User Management | 7 | 7/7 | ✅ |
| US3 — Job Creation & Document Extraction | 9 | 8/9 | ⚠️ |
| US3a — Prompt Management | 7 | 7/7 | ✅ |
| US4 — Application Upload | 4 | 4/4 | ✅ |
| US5 — Scoring Pipeline | 6 | 6/6 | ✅ |
| US6 — Ranked Lists & Detail | 4 | 4/4 | ✅ |
| US7 — Manual Review | 6 | 6/6 | ✅ |
| US8 — Monitoring & Pipeline Visibility | 5 | 5/5 | ✅ |
| Authorization & Security | 4 | 4/4 | ✅ |
| Infrastructure & Storage | 3 | 3/3 | ✅ |
| **Total** | **62** | **61/62** | **⚠️** |

---

## US1 — Authentication & Dashboard

- [x] **CHK001** — Both stacks implement login with username/password, secure password hashing (SHA-256), and session management  
  _Stack A_: `server/routes/auth.ts` POST `/login` with SHA-256 hash; `src/components/LoginForm.tsx`  
  _Stack B_: `AuthEndpoints.cs` POST `/login` with hash verification; `Login.razor`

- [x] **CHK002** — Both stacks redirect unauthenticated users to a login screen and restore sessions on reload  
  _Stack A_: `src/App.tsx` checks `api.getCurrentUser()` on mount  
  _Stack B_: `App.razor` / layout checks auth state, redirects to `/login`

- [x] **CHK003** — Both stacks implement logout that clears session and returns to login  
  _Stack A_: `server/routes/auth.ts` POST `/logout`; `src/lib/api-real.ts` `logout()`  
  _Stack B_: `AuthEndpoints.cs` POST `/logout`; `ApiClient.cs` `LogoutAsync()`

- [x] **CHK004** — Both stacks enforce role-based dashboard filtering: admin sees all departments, recruiter sees only their department  
  _Stack A_: `src/components/DashboardView.tsx` filters by `currentUser.department`  
  _Stack B_: `Dashboard.razor` department filter dropdown

- [x] **CHK005** — Both stacks display job cards on the dashboard with title, department, organisation, and status  
  _Stack A_: `src/components/JobCard.tsx` with colored border, progress bar, application count  
  _Stack B_: `Dashboard.razor` job card rendering with stats

- [x] **CHK006** — Both stacks display system-wide stats on the dashboard (total jobs, applications, queued, completed, failed)  
  _Stack A_: `src/components/DashboardView.tsx` with `StatCard` components  
  _Stack B_: `Dashboard.razor` stats cards with SignalR real-time updates

- [x] **CHK007** — Both stacks seed a default admin user on first launch (username: `admin`, SHA-256 hashed password, role: admin, department: all)  
  _Stack A_: `server/services/init-users.ts` seeds `admin:adm1n99`  
  _Stack B_: `Program.cs` seeds default admin with hash-verified password

---

## US2 — User Management

- [x] **CHK008** — Both stacks implement full user CRUD (create, read, update, delete) restricted to admin role  
  _Stack A_: `server/routes/users.ts` GET/POST/PUT/DELETE with RBAC middleware  
  _Stack B_: `UsersEndpoints.cs` with AdminOnly policy; `CreateUserCommand`, `UpdateUserCommand`, `DeleteUserCommand`

- [x] **CHK009** — Both stacks implement user creation with username, role, department, and initial password  
  _Stack A_: `src/components/UserManagementDialog.tsx` create form → `api.createUser()`  
  _Stack B_: `UserManagement.razor` create user form; `CreateUserCommand` with validator

- [x] **CHK010** — Both stacks implement admin-initiated password reset  
  _Stack A_: `server/routes/users.ts` POST `reset-password`  
  _Stack B_: `UsersEndpoints.cs` POST `/{id}/reset-password`; `ResetPasswordCommand`

- [x] **CHK011** — Both stacks implement self-service password change with old/new/confirm validation  
  _Stack A_: `src/components/ChangePasswordDialog.tsx` with toast feedback  
  _Stack B_: `AuthEndpoints.cs` POST `/change-password`; `ChangePasswordCommand`; UI dialog

- [x] **CHK012** — Both stacks implement recruiter password-reset requests with admin approval workflow  
  _Stack A_: `server/routes/users.ts` GET/POST/PUT `/reset-requests` fully implemented; `UserManagementDialog.tsx` shows pending requests; `UserMenu.tsx` exposes "Request Password Reset" in the dropdown for logged-in users; `LoginForm.tsx` exposes an unauthenticated forgot-password entry point  
  _Stack B_: `UsersEndpoints.cs` exposes GET/POST/PUT on `/api/users/reset-requests`; `GetResetRequestsQuery` is fully implemented (calls `_userRepository.GetResetRequestsAsync()`); `UserMenu.razor` exposes "Request Password Reset" for logged-in non-admin users; `Login.razor` exposes a matching unauthenticated forgot-password entry point

- [x] **CHK013** — Both stacks display a user menu with name, role badge, department, and controls for logout, change password, and user management (admin-only)  
  _Stack A_: `src/components/UserMenu.tsx` — name, role badge, department, Change Password, **Request Password Reset**, Manage Users (admin), Sign Out  
  _Stack B_: `dotnet/src/Web.Client/Components/UserMenu.razor` — name, role badge, department, Change Password, **Request Password Reset** (non-admin only), Manage Users (admin), Logout

- [x] **CHK013a** — Both stacks surface a password-reset request entry point on the login page for users who cannot log in  
  _Stack A_: `src/components/LoginForm.tsx` — "forgot password - request password reset" button opens a confirmation dialog; calls `POST /api/auth/request-password-reset` anonymously  
  _Stack B_: `dotnet/src/Web.Client/Pages/Login.razor` — matching button and confirmation dialog; calls `POST /api/auth/request-password-reset` anonymously

---

## US3 — Job Creation & Document Extraction

- [x] **CHK014** — Both stacks implement job creation form with title, jobCode, department, organisation, and posting date  
  _Stack A_: `src/components/CreateJobDialog.tsx` form fields  
  _Stack B_: `CreateJobDialog.razor` with 2 tabs (Upload Spec, Manual Entry); `CreateJobCommand`

- [x] **CHK015** — Both stacks implement job specification document upload with LLM-based extraction via `AWR_SEQ_API_ENDPOINT/assess/passthrough`  
  _Stack A_: `server/routes/jobs.ts` POST `/extract-spec` with `EXTRACT_SPEC_SYSTEM_PROMPT`; `CreateJobDialog.tsx` file upload  
  _Stack B_: `JobsEndpoints.cs` POST `/extract-spec` lines 35–119; `CreateJobDialog.razor` spec upload tab

- [x] **CHK016** — Both stacks extract title, description, department, organisation, must-haves, desired criteria, and rubric categories from uploaded documents  
  _Stack A_: `server/routes/jobs.ts` system prompt defines extraction schema  
  _Stack B_: `JobsEndpoints.cs` maps LLM response to `ExtractSpecResult` DTO

- [x] **CHK017** — Both stacks implement separate rubric document upload with extraction via `/extract-rubric` endpoint  
  _Stack A_: `server/routes/jobs.ts` POST `/extract-rubric`; `src/lib/api-real.ts` `extractRubric()`  
  _Stack B_: `JobsEndpoints.cs` POST `/extract-rubric` lines 120–214; `ApiClient.cs` `ExtractRubricAsync()`

- [x] **CHK018** — Both stacks generate a draft rubric (60% must-haves, 40% other) when no rubric is present in the uploaded document  
  _Stack A_: `server/routes/jobs.ts` extraction system prompt instructs generation  
  _Stack B_: `JobsEndpoints.cs` extraction logic with rubric generation

- [x] **CHK019** — Both stacks show a loading indicator during extraction and surface extraction errors to the user (FR-044)  
  _Stack A_: `CreateJobDialog.tsx` loading state during extraction, error display  
  _Stack B_: `CreateJobDialog.razor` loading indicator and error messaging

- [x] **CHK020** — Both stacks warn when rubric document title mismatches job title  
  _Stack A_: `CreateJobDialog.tsx` title mismatch warning  
  _Stack B_: `CreateJobDialog.razor` mismatch detection and user advisory

- [x] **CHK021** — Both stacks implement rubric category management (add/remove rows, weights summing to 1.0) and must-have criteria list management  
  _Stack A_: `CreateJobDialog.tsx` rubric categories with weight inputs, must-haves list  
  _Stack B_: `CreateJobDialog.razor` rubric management with sum-to-1.0 validation (FR-028)

- [ ] **CHK059** — Both stacks implement the `UploadRubricDialog` component as a standalone dialog for rubric document upload  
  _Stack A_: `src/components/UploadRubricDialog.tsx` exists and is wired from `JobDetailView.tsx` as a standalone dialog  
  _Stack B_: Rubric upload is integrated into `CreateJobDialog.razor` rather than implemented as a standalone dialog component  
  _Note_: Functional parity exists for rubric upload and extraction, but the UI structure differs. This is a job-creation UX mismatch, not an infrastructure/storage gap.

---

## US3a — Prompt Management

- [x] **CHK022** — Both stacks implement prompt creation with three methods: manual authoring, file import/upload, and AI-assisted generation via `AWR_SEQ_API_ENDPOINT`  
  _Stack A_: `server/routes/prompts.ts` POST create + POST generate; `PromptManagement.tsx` tabs  
  _Stack B_: `PromptEndpoints.cs` POST `/` and POST `/{id}/generate`; `PromptManagement.razor` create manual/file/AI

- [x] **CHK023** — Both stacks implement immutable prompt versioning: editing creates a new revision, original is unchanged  
  _Stack A_: `server/routes/prompts.ts` revision management  
  _Stack B_: `PromptEndpoints.cs` POST `/{id}/edit` creates new revision

- [x] **CHK024** — Both stacks enforce single-active-prompt rule: activating a new prompt deactivates the previous one  
  _Stack A_: `server/routes/prompts.ts` PUT activates/deactivates  
  _Stack B_: `PromptEndpoints.cs` POST `/{id}/activate`; `ActivatePromptCommand` deactivates previous

- [x] **CHK025** — Both stacks implement prompt rating (0–5) and comments per revision  
  _Stack A_: `server/routes/prompts.ts` rating endpoint; `PromptManagement.tsx` rating stars  
  _Stack B_: `PromptEndpoints.cs` POST `/{id}/rate`; `RatePromptCommand`; `PromptManagement.razor` rating UI

- [x] **CHK026** — Both stacks implement prompt test workflow: upload test applications, score with active prompt, mark results as test cases excluded from production lists  
  _Stack A_: `server/routes/prompts.ts` POST `test-run`; `PromptManagement.tsx` test tab  
  _Stack B_: `PromptEndpoints.cs` test run endpoints; `PromptTestRunner.razor`; `PromptTestRun` entity with status tracking

- [x] **CHK027** — Both stacks implement production approval gate: prompt eligible only after manual review of test cases completes without score changes (FR-032, FR-039, FR-040)  
  _Stack A_: `server/routes/prompts.ts` POST `approve`; `server/services/pipeline.ts` enforces production-approved prompt gate  
  _Stack B_: `PromptEndpoints.cs` POST `/{id}/approve`; `ApprovePromptForProductionCommand`

- [x] **CHK028** — Both stacks display prompt revision list with version number, creation date, active status, source, and rating  
  _Stack A_: `PromptManagement.tsx` revision list with status badges (draft/active/production-approved)  
  _Stack B_: `PromptManagement.razor` version list with status/source/rating/date columns

---

## US4 — Application Upload

- [x] **CHK029** — Both stacks implement bulk file upload with drag-and-drop, file type validation, and size limit (≤2MB per file)  
  _Stack A_: `src/components/UploadApplicationsDialog.tsx` drag-drop, validates type/size  
  _Stack B_: `UploadApplications.razor` drag-drop file input; accepts PDF, DOCX, MD; max 2MB

- [x] **CHK030** — Both stacks compute SHA-256 cryptographic fingerprints for duplicate detection  
  _Stack A_: `server/services/document-storage.ts` `computeFingerprint()` SHA-256  
  _Stack B_: Application upload command with fingerprint computation

- [x] **CHK031** — Both stacks store application documents with metadata (file type, file size, file name, upload timestamp)  
  _Stack A_: `server/services/document-storage.ts` `storeDocument()` creates `ApplicationDocument`  
  _Stack B_: `UploadApplicationsCommand` stores `ApplicationDocument` entities via EF Core

- [x] **CHK032** — Both stacks show per-file upload progress and queue applications with status "Queued"  
  _Stack A_: `UploadApplicationsDialog.tsx` progress bar, status checkmarks  
  _Stack B_: `UploadApplications.razor` upload progress and fingerprint tracking

---

## US5 — Scoring Pipeline

- [x] **CHK033** — Both stacks implement document extraction that produces normalised text from uploaded documents  
  _Stack A_: `server/workers/extraction.ts` `runExtraction()` creates `ExtractionArtifact` with markdown  
  _Stack B_: Extraction logic with `ExtractionArtifact` entity (confidence score, processing status)

- [x] **CHK034** — Both stacks implement N configurable scoring passes per application (default N=3) with per-category breakdown  
  _Stack A_: `server/workers/scoring.ts` `runScoring()` runs N passes; generates sub-scores for rubric categories  
  _Stack B_: Scoring pipeline with `ScoringRun` entity recording run index, total score, per-category scores

- [x] **CHK035** — Both stacks record must-have evaluation, evidence citations, improvement tips, and AI model metadata per scoring run  
  _Stack A_: `server/workers/scoring.ts` scoring run records  
  _Stack B_: `ScoringRun` entity with full metadata per FR-010

- [x] **CHK036** — Both stacks implement configurable aggregation strategy (median/mean/weighted) with variance computation  
  _Stack A_: `server/workers/aggregation.ts` `runAggregation()` applies strategy, computes variance  
  _Stack B_: `AggregatedResult` entity with `AggregationStrategy` enum; `JobConfigVersion.AggregationStrategy`

- [x] **CHK037** — Both stacks implement pipeline orchestration (extraction → scoring → aggregation) with retry logic and exponential backoff  
  _Stack A_: `server/services/pipeline.ts` `createPipelineOrchestrator()` with retry logic  
  _Stack B_: Pipeline orchestration in application layer

- [x] **CHK038** — Both stacks auto-flag applications as "Needs Manual Review" when variance exceeds the configured threshold  
  _Stack A_: Pipeline logic sets status based on variance threshold  
  _Stack B_: Application status transition logic with `VarianceThreshold` from `JobConfigVersion`

---

## US6 — Ranked Lists & Application Detail

- [x] **CHK039** — Both stacks display applications sorted by score with sortable columns and status filtering  
  _Stack A_: `src/components/ApplicationsTable.tsx` sortable by finalScore/createdAt, status badges  
  _Stack B_: Application list with filtering and sorting via `GetApplicationsQuery`

- [x] **CHK040** — Both stacks implement application detail view showing original documents, extracted text, all N scoring runs, and aggregated result  
  _Stack A_: `src/components/ApplicationDetail.tsx` tabs for extraction, scoring runs (breakdown), aggregated result  
  _Stack B_: `ApplicationDetail.razor` (FR-025) — documents, extracted text, scoring runs, aggregated decision

- [x] **CHK041** — Both stacks display evidence citations and improvement tips in the application detail  
  _Stack A_: `ApplicationDetail.tsx` evidence section in scoring run detail  
  _Stack B_: Application detail with evidence and tips per FR-025

- [x] **CHK042** — Both stacks provide navigation from application detail to manual review interface  
  _Stack A_: `ApplicationDetail.tsx` "Manual Review" navigation option  
  _Stack B_: Application detail with link to `ManualReview.razor`

---

## US7 — Manual Review

- [x] **CHK043** — Both stacks implement three-pane manual review layout: job spec/rubric (left), scoring form (centre), application content (right)  
  _Stack A_: `src/components/ManualReviewView.tsx` three-pane layout  
  _Stack B_: `ManualReview.razor` three-pane layout per FR-026

- [x] **CHK044** — Both stacks implement per-category rubric scoring with point allocation and live weighted score recalculation  
  _Stack A_: `ManualReviewView.tsx` rubric scoring inputs with live score update  
  _Stack B_: `ManualReview.razor` per-category rubric scoring with live recalculation (FR-026)

- [x] **CHK045** — Both stacks persist manual review data (rubric scores, comments, adjusted final score) and restore on reload  
  _Stack A_: API calls to save/load manual review data  
  _Stack B_: `SaveManualReviewCommand`; `ManualReview` entity persisted via EF Core

- [x] **CHK046** — Both stacks record an immutable audit trail for every manual scoring change (reviewer, timestamp, category, previous/new value)  
  _Stack A_: `ManualReviewView.tsx` audit trail of modifications  
  _Stack B_: `ProcessingEvent` entity appended for each manual review action

- [x] **CHK056** — Both stacks pre-populate the rubric scoring form with AI-assigned per-category scores (averaged across all scoring runs) when AI scoring results exist and no manual review has been previously saved. A banner MUST be visible indicating AI pre-population is active and showing the aggregated score and variance (US7 scenario 6, FR-014)  
  _Stack A_: `src/lib/stackb-scoring.ts` `buildStackBManualReviewPrepopulation()` averages category scores and sets points; `ManualReviewView.tsx` renders pre-population banner  
  _Stack B_: `ManualReview.razor` `PrePopulateFromAiAsync()` averages category scores and pre-populates rubric with evidence snippets. Parser regression fixed: case-insensitive evidence extraction, semantic field name preference for category/evidence extraction.

- [x] **CHK057** — Both stacks pre-populate each rubric category's notes/comment field with the AI evidence snippets (direct quotes/citations from the CV) associated with that category, sourced from `evidenceCitations` in the scoring runs (US7 scenario 6, FR-014)  
  _Stack A_: `src/lib/stackb-scoring.ts` `collectEvidenceByRubricCategory()` + `buildStackBManualReviewPrepopulation()` appends evidence under an "Evidence" heading in each category comment  
  _Stack B_: `ManualReview.razor` `PrePopulateFromAiAsync()` — Fixed: parser now uses `TryGetPropertyCaseInsensitive` for evidence lookup, `ExtractCategoryName` prefers category/name/label properties, `ExtractStringField` prefers evidence/justification properties. Empty snippets filtered. Evidence dedup hardened with trim + case-insensitive comparison.

- [x] **CHK047** — Both stacks implement a pipeline visualiser showing stage counts (extraction → scoring → aggregation → complete)  
  _Stack A_: `src/components/PipelineVisualizer.tsx` visual stages with progress indicators  
  _Stack B_: Dashboard/job detail pipeline stage visualization

- [x] **CHK048** — Both stacks implement a failure queue view with DLQ items, error details, retry button, and 30-second auto-refresh  
  _Stack A_: `src/components/FailureQueueView.tsx` loads DLQ items, retry button, 30s auto-refresh  
  _Stack B_: Failure queue view per FR-027; `RetryDlqItemCommand`; auto-refresh

- [x] **CHK049** — Both stacks implement DLQ retry that moves items back to the processing queue  
  _Stack A_: `server/routes/dlq.ts` POST `/dlq/:itemId/retry` re-triggers pipeline  
  _Stack B_: `RetryDlqItemCommand` handler removes from DLQ, re-queues

- [x] **CHK050** — Both stacks implement system stats aggregation (queued/processing/completed/failed) with auto-refresh  
  _Stack A_: `server/routes/stats.ts` GET `/api/stats`; dashboard polls  
  _Stack B_: Stats endpoint; `Dashboard.razor` SignalR real-time updates

- [x] **CHK051** — Both stacks implement an audit trail/processing event ledger with filtering by entity type, event type, and date range  
  _Stack A_: `server/routes/audit.ts` GET `/api/audit` with filters; `server/services/audit.ts` `appendEvent()`  
  _Stack B_: `ProcessingEventRepository`; `ProcessingEvent` entity with full audit schema

---

## Authorization & Security

- [x] **CHK052** — Both stacks enforce authentication middleware on all API routes except login  
  _Stack A_: `server/middleware/auth.ts` `createAuthMiddleware()` applied globally; login excluded  
  _Stack B_: `Program.cs` cookie auth configured; auth middleware on all endpoints

- [x] **CHK053** — Both stacks enforce RBAC: admin-only routes (user management, password reset) return 403 for non-admins  
  _Stack A_: `server/middleware/rbac.ts` `requireRole()` returns 403  
  _Stack B_: `AdminOnly` policy on `UsersEndpoints`; returns 403

- [x] **CHK054** — Both stacks store passwords as one-way SHA-256 hashes (no plaintext storage)  
  _Stack A_: `server/routes/auth.ts` SHA-256 hashing on login/create  
  _Stack B_: SHA-256 hashing in `AuthEndpoints`, `CreateUserCommand`

- [x] **CHK055** — Both stacks proxy all AI model calls through the backend; no client-side API keys (FR-017)  
  _Stack A_: `server/routes/jobs.ts` calls `AWR_SEQ_API_ENDPOINT` from Express; no client-side keys  
  _Stack B_: `JobsEndpoints.cs` calls `AWR_SEQ_API_ENDPOINT` from ASP.NET; no client-side keys

---

## Infrastructure & Storage

- [x] **CHK060** — Both stacks implement a swappable storage backend for local development and production (FR-018)  
  _Stack A_: `server/storage/factory.ts` selects `LocalKVStorage` or `AzureSQLStorage` via `STORAGE_PROVIDER` env  
  _Stack B_: EF Core with `AppDbContext`; SQLite for dev, Azure SQL for production via connection string

- [x] **CHK061** — Both stacks implement structured error handling with consistent JSON error responses  
  _Stack A_: `server/middleware/error-handler.ts` returns `{ error, message, statusCode }`  
  _Stack B_: Exception handling middleware in ASP.NET pipeline; structured error DTOs

- [x] **CHK062** — Both stacks implement request validation with descriptive error messages on invalid input  
  _Stack A_: `server/middleware/validate.ts` Zod schema validation returns 400 with detail  
  _Stack B_: FluentValidation / MediatR pipeline behaviors; returns 400 with validation errors

---

## Notes

- **Tasks.md vs Reality**: The `tasks.md` for feature 001 marks many items unchecked, but the implementation audit found the feature broadly complete.
- **Outstanding Items**: The only checklist item still marked partial is CHK059, where functional parity exists but the UI structure differs: Stack A uses a standalone `UploadRubricDialog.tsx`, while Stack B integrates rubric upload within `CreateJobDialog.razor`.
- **Architectural Differences**: Stack B uses Clean Architecture (Domain → Infrastructure → Application → Web) with CQRS/MediatR, while Stack A uses Express middleware with KV storage. These are expected structural differences per the constitution — behavioral parity is what matters.
- **Real-Time Updates**: Stack B uses SignalR for real-time dashboard updates; Stack A uses polling. Both achieve the same user-facing behavior (stats refresh within 30 seconds per FR-015).
