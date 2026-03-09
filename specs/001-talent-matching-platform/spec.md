# Feature Specification: Talent Matching Platform

**Feature Branch**: `001-talent-matching-platform`
**Created**: 2026-03-03
**Updated**: 2026-03-09
**Status**: Active (brownfield — existing codebase)
**Source**: PRD.md, INTEGRATION.md, README.md, AUTHENTICATION.md, MANUAL_REVIEW_SUMMARY.md

---

## User Scenarios & Testing

### User Story 1 — Recruiter authenticates and reaches their dashboard (Priority: P1)

A recruiter opens the app, enters username and password, and lands on a dashboard filtered to show
only jobs from their department. An admin sees all jobs regardless of department.

**Why this priority**: Gate to every other feature; nothing is accessible without auth.

**Independent Test**: Login with admin credentials, verify all-department dashboard. Create a
recruiter user scoped to "Engineering", log in, verify only Engineering jobs appear.

**Acceptance Scenarios**:

1. **Given** the app loads, **When** no session exists, **Then** the login screen is shown with
   username and password fields.
2. **Given** valid credentials, **When** submitted, **Then** user is redirected to dashboard with
   role-filtered jobs.
3. **Given** invalid credentials, **When** submitted, **Then** an error message is shown; no
   navigation occurs.
4. **Given** a logged-in user, **When** they click "Logout", **Then** session is cleared and the
   login screen is shown.
5. **Given** an admin, **When** they view the dashboard, **Then** all departments' jobs are visible.
6. **Given** a recruiter, **When** they view the dashboard, **Then** only their department's jobs
   appear.
7. **Given** the Web.Server is running, **When** a browser navigates to the root URL (`/`), **Then**
   the Blazor WebAssembly UI is served (not a 404 or blank page), and API endpoints remain
   accessible under their `/api/` prefix.

---

### User Story 2 — Admin manages users and passwords (Priority: P1)

An admin can create users, reset any user's password, delete users, and approve/reject recruiter
password-reset requests. All users can change their own password.

**Why this priority**: Required for initial demo setup and recruiter onboarding.

**Independent Test**: As admin, create a recruiter, reset their password, then approve a pending
reset request from a different user.

**Acceptance Scenarios**:

1. **Given** admin is logged in, **When** they open User Management, **Then** a list of all users
   is shown with Create, Delete, and Reset controls.
2. **Given** admin creates a user, **When** they supply username, role, department, and initial
   password, **Then** the user can log in with those credentials.
3. **Given** a recruiter, **When** they request a password reset, **Then** a pending request appears
   in the admin's User Management view.
4. **Given** a pending reset request, **When** admin approves with a new password, **Then** the
   user can log in with the new password.
5. **Given** any logged-in user, **When** they change their own password (providing the correct
   current password), **Then** the change persists and old password no longer works.
6. **Given** admin tries to delete themselves, **Then** the action is blocked.

---

### User Story 3 — Recruiter creates a job with rubric and scoring config (Priority: P2)

A recruiter fills in job details (title, department, organisation, posting date), uploads or
manually specifies a job specification, uploads and reviews/approved or defines a scoring rubric document with weighted categories, sets must-have criteria, and desired criteris, and configures the number of scoring runs and aggregation strategy. The user interface provides the capability to optionally upload a job specification, a separate job scoring rubric document or both.

**Why this priority**: Core data creation step; all downstream features depend on a configured job.

**Independent Test**: Create a job end-to-end; job card appears on dashboard with correct metadata.

**Acceptance Scenarios**:

1. **Given** a recruiter clicks "Create Job", **When** they complete the form, **Then** a new job
   card appears on the dashboard with the correct title, department, organisation, and posting date.
2.  **Given** a job form, **When** a job specification document is uploaded, **Then** the system extracts
   Job Title, Job Description, if present, the department , organization. The system further extracts all "must have" requirements, recommended or desired qualifications and experience items. if a rubric is present in the Job Specification document, extract the rubric categories and its weights that sum to 1.0. If no rubric is present within the document and no separate rubric document was uploaded, generate a draft rubric with weights that sum to 1.0 based on the requirements, assigning 60% of the weights to the "must have" requirements. The supported document types are pdf, jpg , md, txt, docx and the system calls the external API at `AWRSEQAPI_ENDPOINT/assess/passthrough` to perform this extraction (API documentation is available at `AWRSEQAPI_ENDPOINT/docs`). The generated or extracted rubric MUST be stored and associated with the job as part of the job configuration version.
3. **Given** a job form, **When** a rubric document is uploaded, **Then** the system extracts the job title and rubric categories with weights that sum to 1.0 and presents them for confirmation. Should the Job title of the rubric document not match the Job Title of the job form, present a warning to the user explaining the mismatch and advise the user to correct and upload the corrected the rubric document and inform then that the existing rubric document will be discarded.
4. **Given** rubric categories are confirmed, **When** the job is saved, **Then** the job
   configuration is persisted with the rubric (including the raw API response from generation, if
   applicable), must-have criteria, scoring run count, aggregation strategy, and longlist/shortlist
   thresholds. The stored rubric serves as the approved rubric for downstream prompt generation
   (US3a).
5. **Given** a job is already created, **When** configuration is changed, **Then** a new version of
   the configuration is created without affecting in-progress scoring on the previous version.
6. **Given** a valid job, **When** the jobs list is viewed, **Then** the job card shows the
   organisation name and days since posting.

---

### User Story 3a — Recruiter creates, tests, and approves a scoring prompt (Priority: P1)

After a job has been created (US3) and before any applications can be scored (US5), the recruiter
must create, test, and approve a scoring prompt for use by the external scoring API. This step is a
hard prerequisite for production scoring — the pipeline MUST NOT score applications for a job that
does not have an approved, active prompt.

Once a job exists, the job detail view enables prompt management controls. The recruiter can
choose to: (a) manually author a prompt, (b) import/upload a prompt from a file, or (c) generate
a draft prompt. When generating, the system uses the approved scoring rubric from US3 and calls the
external API endpoint defined by the environment variable `AWRSEQAPI_ENDPOINT` to produce a draft.
The recruiter MUST review the generated prompt, may optionally edit it, and MUST explicitly approve
it before it can be used.

Prompts are stored per-job and versioned. A dropdown lists all existing prompt revisions for the
job. Selecting an existing prompt allows the recruiter to either activate it as-is or edit it and
save the result as a new revision. Only one prompt may be active for a job at any time.

The recruiter can assign a rating (0–5) and add comments to any prompt revision to guide future
improvements and track prompt quality over time.

Once a prompt has been created and set as active, the recruiter can test it by uploading a small
set of real example applications through the same upload and scoring flow described in US4/US5.
The resulting scored applications are marked as **test cases** (not production data). The recruiter
reviews these test-case results using the manual review process (US7). If the manual review
confirms the scores are acceptable without requiring changes, the prompt is approved for production
scoring. If changes are needed, the recruiter revises the prompt and repeats testing until
satisfied.

**Why this priority**: The scoring pipeline depends on an approved prompt; without this gate,
scoring results are unreliable and unauditable.

**Independent Test**: Create a job with a rubric. Generate a draft prompt via the API. Edit the
prompt, save, and activate it. Upload 3 test applications, verify they are scored and flagged as
test cases. Perform manual review on each. Approve the prompt for production. Verify production
scoring can now proceed and test-case results do not appear in production ranked lists.

**Acceptance Scenarios**:

1. **Given** a job has been created (US3 complete), **When** the recruiter views the job detail,
   **Then** prompt management controls (create, import/upload, generate) are enabled.
2. **Given** no job exists, **When** the recruiter views the prompt management area, **Then** all
   prompt controls are disabled with a message indicating a job must be created first.
3. **Given** the recruiter selects "Generate Prompt", **When** the job has an approved scoring
   rubric, **Then** the system calls the `AWRSEQAPI_ENDPOINT` API with the rubric and returns a
   draft prompt for review.
4. **Given** a draft prompt is displayed, **When** the recruiter edits and clicks "Save", **Then**
   the prompt is stored as a new versioned revision for the job.
5. **Given** the recruiter clicks "Approve" on a prompt revision, **Then** that revision becomes
   the active prompt for the job; any previously active prompt is deactivated.
6. **Given** multiple prompt revisions exist, **When** the recruiter opens the prompt dropdown,
   **Then** all revisions are listed with version number, creation date, active status, and rating.
7. **Given** the recruiter selects an existing prompt revision, **When** they choose "Activate",
   **Then** it becomes the active prompt and the previously active prompt is deactivated.
8. **Given** the recruiter selects an existing prompt revision, **When** they choose "Edit",
   **Then** the prompt text is loaded into the editor; saving creates a new revision (the original
   is unchanged).
9. **Given** only one prompt can be active at a time, **When** a new prompt is activated, **Then**
   the system confirms the switch and records the change in the audit trail.
10. **Given** a prompt revision, **When** the recruiter assigns a rating (0–5) and/or adds a
    comment, **Then** the rating and comment are persisted against that revision and visible in the
    prompt revision list.
11. **Given** an active prompt exists, **When** the recruiter initiates a "Test Prompt" workflow,
    **Then** the system allows uploading example applications using the same process as US4.
12. **Given** test applications are uploaded, **When** they are scored via the pipeline (US5),
    **Then** the results are marked as **test cases** and are excluded from production ranked lists.
13. **Given** test-case results are available, **When** the recruiter opens manual review (US7),
    **Then** they can review each test case with the same three-pane interface.
14. **Given** the recruiter completes manual review of all test cases, **When** no score changes
    were required, **Then** the prompt is eligible for production approval and an "Approve for
    Production" button is enabled.
15. **Given** manual review of test cases required score changes, **Then** the prompt is NOT
    eligible for production approval; the recruiter must revise the prompt and re-test.
16. **Given** no prompt has been approved for production for a job, **When** production scoring is
    attempted, **Then** the system blocks scoring and displays a message requiring prompt approval.
17. **Given** the recruiter imports/uploads a prompt file, **When** the file is valid, **Then** its
    content is loaded into the editor for review, editing, and saving as a new revision.

---

### User Story 4 — Recruiter uploads applications in bulk (Priority: P2)

A recruiter selects an active job and uses the upload dialog to drag-and-drop or bulk-select
document files (one or more per candidate). Each uploaded application is validated, fingerprinted,
stored, and queued for processing.

**Why this priority**: The pipeline cannot run without ingested applications.

**Independent Test**: Upload 5 application files; verify all appear in the job's application list
with status "Queued" and correct fingerprints.

**Acceptance Scenarios**:

1. **Given** the upload dialog is open, **When** files are dropped or selected, **Then** each file is
   validated for allowed type and size; invalid files show an error inline.
2. **Given** valid files, **When** upload completes, **Then** each application has status "Queued",
   a unique cryptographic fingerprint, file type, file size, and upload timestamp recorded.
3. **Given** a duplicate file (matching fingerprint), **When** uploaded, **Then** the system warns
   about the duplicate and allows the recruiter to link or treat as separate.
4. **Given** a large batch, **When** upload is initiated, **Then** a progress indicator per file is
   shown and the interface does not time out.

---

### User Story 5 — AI scoring pipeline runs and results are visible (Priority: P2)

Once applications are queued, the system extracts documents to a normalised text format, runs N
configurable scoring passes per application (each producing a score, category breakdown, must-have
evaluation, evidence citations, and improvement tips), then aggregates runs into a final decision.

**Why this priority**: Core value proposition of the platform.

**Independent Test**: Trigger scoring for a single application; verify N scoring run records exist,
each with score, evidence citations, must-have results, and AI model metadata; verify aggregated
result is present with final decision.

**Acceptance Scenarios**:

1. **Given** a queued application, **When** extraction succeeds, **Then** a normalised text version
   of the document exists with a confidence score.
2. **Given** a successful extraction, **When** N scoring runs execute, **Then** each run record
   contains a run index, total score, per-category scores, must-have evaluation, evidence citations,
   improvement tips, and AI model and prompt version identifiers.
3. **Given** N runs complete, **When** aggregation runs (using the configured strategy), **Then** an
   aggregated result exists with a final score, decision (Eligible, Excluded, or Needs Manual
   Review), variance, confidence, and a consolidated rationale.
4. **Given** variance exceeds the configured threshold, **Then** the application is flagged as
   "Needs Manual Review".
5. **Given** an extraction failure, **Then** the application status becomes "Extraction Failed"; the
   error reason is stored and a manual retry is available.
6. **Given** a scoring run failure after maximum retries, **Then** the item moves to the failure
   queue with error details.

---

### User Story 6 — Recruiter views ranked lists and drills into application detail (Priority: P2)

The job detail view shows a longlist, shortlist, and exclusions with sortable columns and filters.
Clicking a candidate opens a full drill-down showing documents, extracted text, all N scoring
runs, evidence, final decision, and improvement tips.

**Why this priority**: Decision-making view; core recruiter workflow.

**Independent Test**: Navigate to a completed job; verify longlist/shortlist/exclusions tabs contain
correctly categorised candidates; drill into one application and verify all run details are shown.

**Acceptance Scenarios**:

1. **Given** a job with completed applications, **When** the Lists tab is opened, **Then** longlist,
   shortlist, and exclusions are shown in separate tabs with correct counts.
2. **Given** a list of applications, **When** sorted by score descending, **Then** highest-scoring
   applications appear first.
3. **Given** variance filter applied, **When** threshold set, **Then** only applications above that
   variance are shown.
4. **Given** a candidate row is clicked, **Then** a detail view shows: original documents, extracted
   text, all N individual scoring runs (scores and evidence), and the aggregated final decision.
5. **Given** an excluded candidate, **When** their detail is viewed, **Then** justification and
   improvement tips are prominently displayed.
6. **Given** an eligible application, **When** viewed, **Then** a "Manual Review" option navigates
   to the three-pane manual review interface.

---

### User Story 7 — Recruiter performs a manual review (Priority: P3)

The manual review interface has three resizable panes: job specification (left), rubric scoring form
(centre), and application content (right). The recruiter assigns points per category, adds comments,
saves the review, and every change is appended to an immutable audit trail.

**Why this priority**: Required for high-variance and edge-case applications; provides defensibility.

**Independent Test**: Open manual review for a flagged application; allocate points in all
categories; save; reload and verify the saved scores and comments persist with audit trail entries.

**Acceptance Scenarios**:

1. **Given** a recruiter opens manual review, **Then** all three panes are visible and independently
   scrollable.
2. **Given** points are allocated to a rubric category, **Then** the live score updates immediately.
3. **Given** save is clicked, **Then** the review data (including rubric scores, overall comment,
   and audit trail) is persisted against the application.
4. **Given** a page reload, **When** reviewing the same application, **Then** previous rubric scores
   and comments are pre-populated.
5. **Given** any scoring change, **Then** an audit entry is appended recording the reviewer,
   timestamp, category, previous value, and new value.

---

### User Story 8 — Admin/recruiter monitors the processing pipeline (Priority: P3)

The dashboard shows system-wide stats (queued, processing, completed, failed), a filterable job
list, a pipeline visualiser per job (extraction → scoring → aggregation), and a failure queue
browser with retry controls. Stats refresh automatically.

**Why this priority**: Operational oversight for large batch runs.

**Independent Test**: While jobs are processing, verify stats update without manual page refresh;
simulate a failure and verify it appears in the failure queue with a retry option.

**Acceptance Scenarios**:

1. **Given** the dashboard is open, **When** time elapses, **Then** stats refresh automatically
   (within 30 seconds).
2. **Given** filtering by department and organisation, **Then** only matching jobs are shown.
3. **Given** a failed item in the failure queue, **When** "Retry" is clicked, **Then** the item
   moves back to the processing queue and the failure count decreases.
4. **Given** the pipeline visualiser for a job, **Then** the stages (extraction, scoring,
   aggregation, complete) display correct counts and percentage completion.

---

### Edge Cases

- Document format failures: extraction records failure reason; application marked for manual review;
  document preview available for human processing.
- AI model timeout/errors: automatic retry with backoff; maximum attempts exceeded moves item to
  failure queue with full error detail and manual retry option.
- High score variance (>15 points across runs): auto-flagged for manual review with variance
  visualisation.
- Concurrent job configuration changes: versioned configurations ensure in-flight scoring uses the
  original version; new version applies only to new applications.
- Duplicate applications: cryptographic fingerprint detected; system warns and offers
  link-or-separate.
- Large document volumes: pagination, virtual scrolling, progressive content rendering.
- Network interruptions: optimistic updates with rollback; visible sync status indicator.

---

## Requirements

### Functional Requirements

- **FR-001**: System MUST authenticate users with username and password; passwords MUST be stored securely using one-way hashing.
- **FR-002**: System MUST enforce role-based access — recruiters see only their department's jobs; admins see all.
- **FR-003**: Admin MUST be able to create, reset passwords for, and delete other users.
- **FR-004**: System MUST support recruiter-initiated password-reset requests visible to admin.
- **FR-005**: Jobs MUST be created with title, department, organisation, posting date, and a versioned configuration.
- **FR-006**: Job configuration changes MUST create a new version without invalidating in-progress scoring.
- **FR-007**: System MUST accept bulk document upload with per-file cryptographic fingerprinting, file type, and size validation.
- **FR-008**: System MUST flag duplicate applications via fingerprint comparison.
- **FR-009**: System MUST run N configurable scoring passes per application (default N=3).
- **FR-010**: Each scoring run MUST record the score, category breakdown, must-have evaluation, evidence citations, improvement tips, AI model identifier, prompt version, and resource usage metrics.
- **FR-011**: System MUST aggregate N runs using a configurable strategy (median/mean/weighted) and compute variance.
- **FR-012**: Applications with variance above the configured threshold MUST be auto-flagged as "Needs Manual Review".
- **FR-013**: Failed extractions and scoring runs MUST be retried with backoff; items exceeding maximum retries go to the failure queue.
- **FR-014**: Manual review MUST record per-category scores, comments, and produce an immutable audit trail.
- **FR-015**: Dashboard stats MUST auto-refresh at intervals of 30 seconds or less.
- **FR-016**: Longlist, shortlist, and exclusion lists MUST be sortable and filterable.
- **FR-017**: All AI model calls MUST be proxied through the backend; no client-side API keys.
- **FR-018**: System MUST support swappable storage backends for local development and production deployment.
- **FR-019**: The Stack B Web.Server project MUST host the Blazor WebAssembly client — it MUST include the WebAssembly.Server hosting package, serve Blazor framework files and static assets via middleware, and map a fallback route to `index.html` so that the root URL and all client-side routes serve the Blazor UI alongside the API endpoints.
- **FR-020**: The Stack B Web.Server MUST seed a default admin user on first launch (username `admin`, SHA-256 hashed password, role `admin`, department `all`) if no users exist in the database, matching the Stack A behaviour defined in `server/services/init-users.ts` and `AUTHENTICATION.md`.
- **FR-021**: The Stack B Blazor WASM client MUST send cookie credentials with every API request by configuring `BrowserRequestCredentials.Include` via a `DelegatingHandler` and `IHttpClientFactory`, and the Web.Server cookie auth MUST set `HttpOnly`, `SameSite=Strict`, and `SecurePolicy=SameAsRequest` options to ensure cookies persist across same-origin requests.
- **FR-022**: The Stack B Blazor WASM client MUST provide a `UserMenu` component in the layout header displaying the authenticated user's name, role badge, and department, with controls for logout, change password, and (admin-only) user management access — matching the React `UserMenu.tsx` behaviour.
- **FR-023**: The Stack B Blazor WASM client MUST provide accessible navigation entry points for all features: "Create Job" button on Dashboard, "Upload Applications" button on Job Detail, User Management from UserMenu (admin-only), Change Password from UserMenu, and Failure Queue from navigation — ensuring no component is orphaned from the UI.
- **FR-024**: The Stack B Blazor WASM client MUST implement a Change Password dialog with current password, new password, and confirmation fields, calling the change-password API endpoint with success/error feedback — matching US2 acceptance scenario 5.
- **FR-025**: The Stack B Blazor WASM client MUST implement an Application Detail page (`ApplicationDetail.razor`) displaying original documents with download links, extracted text, all N individual scoring runs with per-category breakdown and evidence citations, and the aggregated final decision with rationale and improvement tips — matching US6 acceptance scenario 4 and React `ApplicationDetail.tsx`.
- **FR-026**: The Stack B Manual Review page MUST implement per-category rubric scoring with point allocation inputs for each category, live weighted score recalculation, display of the job's rubric categories and must-have criteria in the left pane, and a chronological audit trail — matching US7 acceptance scenarios 1–5 and React `ManualReviewView.tsx`.
- **FR-027**: The Stack B Blazor WASM client MUST implement a Failure Queue view displaying DLQ items with error details, failure reason, retry count, timestamps, a retry button, and 30-second auto-refresh — matching US8 acceptance scenario 3 and React `FailureQueueView.tsx`.
- **FR-028**: The Stack B Create Job dialog MUST implement rubric category management (add/remove rows, weight inputs with sum-to-1.0 validation), must-have criteria list management (add/remove), and job specification file upload with LLM-based rubric extraction — matching US3 acceptance scenarios 2–3 and React `CreateJobDialog.tsx` / `UploadRubricDialog.tsx`.
- **FR-029**: The Stack B User Management component MUST include a tab or section for pending password reset requests with approve (with new password) and reject controls — matching US2 acceptance scenarios 3–4 and React `UserManagementDialog.tsx`.
- **FR-030**: All Stack B Blazor WASM mutating operations (create user, delete user, reset password, create job, upload applications) MUST check the API response for success before updating local state; MUST show a visible success message on success and an error message with the server-returned reason on failure; and MUST NOT close dialogs or clear form fields when the operation fails — matching the feedback patterns in the React frontend's toast notifications.
- **FR-031**: The Stack B Blazor WASM client MUST verify that newly created users can authenticate immediately — the password hash produced during user creation (via `CreateUserCommand`) MUST be identical to the hash computed during login (via `AuthEndpoints`), and the `ApiClient.CreateUserAsync` method MUST propagate server-side errors (e.g., duplicate username, validation failures) to the calling component.
- **FR-032**: System MUST require an approved, active scoring prompt for a job before any production scoring runs can execute for that job.
- **FR-033**: Prompt management controls MUST be enabled only after a job has been created with an approved scoring rubric.
- **FR-034**: System MUST support three prompt creation methods: manual authoring, file import/upload, and AI-assisted generation via the `AWRSEQAPI_ENDPOINT` external API using the job's approved rubric.
- **FR-035**: All prompts MUST be stored per-job with immutable versioning; editing an existing prompt MUST create a new revision without altering the original.
- **FR-036**: Only one prompt revision MAY be active for a given job at any time; activating a new revision MUST deactivate the previously active one.
- **FR-037**: Recruiters MUST be able to assign a rating (integer 0–5) and add free-text comments to any prompt revision.
- **FR-038**: The system MUST support a prompt testing workflow that scores a set of example applications using the active prompt, marks all resulting scores as test cases, and excludes them from production ranked lists.
- **FR-039**: Test-case results MUST be reviewable through the manual review process (US7); a prompt is eligible for production approval only when manual review of all test cases completes without requiring score changes.
- **FR-040**: The system MUST block production scoring for a job until at least one prompt has been approved for production use via the test-and-review workflow.
- **FR-041**: All prompt lifecycle events (creation, editing, activation, rating, commenting, testing, approval) MUST be recorded in the immutable audit trail with actor, timestamp, and event payload.

### Key Entities

- **User**: A person who accesses the system. Key attributes: username, role (admin or recruiter), full name, email, department, account creation date, and last login. Passwords stored as secure one-way hashes.
- **Job**: A job opening that applications are scored against. Key attributes: job code, title, department, organisation, posting date, status, and a reference to its current configuration version.
- **Job Configuration Version**: A versioned snapshot of a job's scoring setup. Key attributes: rubric (categories with weights), must-have criteria, number of scoring runs, aggregation strategy, longlist/shortlist thresholds, and variance threshold.
- **Application**: A candidate's submission for a specific job. Tracks status through the pipeline (Queued → Extracting → Scoring → Aggregating → Completed / Needs Manual Review / Failed), associated documents, final score, final decision, and variance.
- **Application Document**: An individual file within an application (CV, cover letter, etc.). Tracked with cryptographic fingerprint, file type, file size, file name, and upload timestamp.
- **Extraction Artifact**: The normalised text version of an application's documents, with a confidence score and processing status.
- **Scoring Run**: A single AI scoring pass for an application. Records the run index, total score, per-category scores, must-have evaluation, evidence citations, improvement tips, AI model identifier, prompt version, and resource usage.
- **Aggregated Result**: The combined result of all scoring runs for an application. Contains final score, decision (Eligible / Excluded / Needs Manual Review), variance, confidence, consolidated rationale, and merged improvement tips.
- **Scoring Prompt**: A versioned prompt used by the external scoring API for a specific job. Key attributes: job reference, version number, prompt text, status (draft / active / inactive / production-approved), creation timestamp, last-modified timestamp, author, rating (0–5, nullable), and comments. Only one prompt may be active per job. A prompt must pass the test-and-review workflow before it can be approved for production scoring.
- **Prompt Test Run**: A scoring pipeline execution triggered for prompt testing purposes. Links a set of test-case applications to a specific prompt revision. Tracks test status (pending review / approved / rejected) and is excluded from production result sets.
- **Failure Queue Item**: A processing item that has exceeded retry limits. Records the entity type, failure reason, retry count, and timestamps.
- **Manual Review Data**: A recruiter's manual assessment of an application. Contains per-category rubric scores, overall comment, adjusted final score, and an immutable audit trail of all changes.
- **Processing Event**: An immutable ledger entry recording any state-changing action. Captures the actor, event type, affected entity, payload, and timestamp with a correlation identifier for end-to-end tracing.

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 20,000 applications can be uploaded for a single job within 90 minutes with zero data loss.
- **SC-002**: Each application receives all N scoring runs and a final aggregated result within 15 minutes of upload (at default settings).
- **SC-003**: Dashboard statistics reflect pipeline state within 30 seconds of any status change.
- **SC-004**: Applications with score variance above the configured threshold are automatically flagged for manual review with 100% accuracy.
- **SC-005**: All final scoring decisions are fully traceable — AI model version, prompt version, individual run results, reviewer identity, and timestamp are recorded for every decision.
- **SC-006**: A recruiter can log in, create a job, upload applications, and reach the ranked list view within 10 minutes of first use.
- **SC-007**: Role-based access control is enforced end-to-end: recruiter accounts cannot view, query, or access data for jobs outside their assigned department.
- **SC-008**: Manual review scores persist across page reloads, and every point allocation change is recorded in an immutable per-application audit trail.

---

## Assumptions

- The platform will be hosted in a cloud environment with access to AI/LLM services.
- Initial deployment targets a single-tenant scenario with a defined set of departments and user roles.
- Document formats accepted for upload include common file types (PDF, DOCX, MD); exotic formats are out of scope for initial release.
- The admin user account is pre-created during initial deployment; subsequent users are managed through the admin interface.
- AI model availability and rate limits are managed externally; the platform handles transient failures through retry mechanisms.
- Scoring rubrics are defined per job; there is no global rubric library in the initial release.
- The platform handles English-language documents and job specifications; multi-language support is a future consideration.

---

## Scope & Boundaries

### In Scope

- User authentication and role-based access control (admin and recruiter roles)
- Job creation with versioned scoring configurations
- Bulk document upload and ingestion
- AI-powered multi-run scoring pipeline with aggregation
- Ranked candidate lists with filtering and drill-down
- Three-pane manual review interface with audit trail
- Operational dashboard with pipeline monitoring and failure retry
- Immutable audit ledger for all decisions and actions

### Out of Scope

- Candidate-facing portal or self-service application submission
- Integration with external applicant tracking systems (ATS)
- Email notifications or alerts
- Multi-language document processing
- Advanced analytics or reporting beyond the operational dashboard
- Candidate communication or interview scheduling
- Mobile-native application (responsive web is in scope)
